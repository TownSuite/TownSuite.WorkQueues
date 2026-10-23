using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TownSuite.WorkQueues.Client;
using TownSuite.WorkQueues.Server.Stores;

namespace TownSuite.WorkQueues.Server;

/// <summary>The <c>/api/v1/topics</c> endpoints.</summary>
internal static class TopicEndpoints
{
    public static void MapTopicEndpoints(this IEndpointRouteBuilder app)
    {
        var topics = app.MapGroup("/api/v1/topics/{topic}").RequireAuthorization();

        topics.MapPost("/messages", PublishAsync)
            .AddEndpointFilter(new TopicAccessFilter(TopicAccess.Publish));

        var subscription = topics.MapGroup("/subscriptions/{subscriberId}")
            .AddEndpointFilter(new TopicAccessFilter(TopicAccess.Subscribe));
        subscription.MapPut("", SubscribeAsync);
        subscription.MapGet("", GetSubscriptionAsync);
        subscription.MapDelete("", UnsubscribeAsync);
        subscription.MapGet("/messages", ReceiveAsync);
        subscription.MapPost("/messages/{receipt}/ack", AckAsync);
        subscription.MapPost("/messages/{receipt}/nack", NackAsync);
    }

    private static async Task<IResult> PublishAsync(string topic, HttpRequest request, ITopicStore store,
        IOptions<TopicServerOptions> options, CancellationToken cancellationToken)
    {
        var max = options.Value.MaxMessageSize;
        if (request.ContentLength > max) return TooLarge(max);

        // Read at most one byte past the limit; Content-Length can be absent (chunked).
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max) return TooLarge(max);
        }

        var bytes = buffer.ToArray();
        try
        {
            using var document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return Results.Problem("The body must be JSON.", statusCode: StatusCodes.Status400BadRequest);
        }

        // The request's activity continues the publisher's trace (traceparent header), so storing
        // it links the subscribers' processing to the publisher across data centres.
        var message = new TopicMessage(Guid.NewGuid(), Encoding.UTF8.GetString(bytes), CaptureTraceContext());
        var copies = await store.PublishAsync(topic, message, cancellationToken);
        WorkQueueMetrics.RecordPublished(TopicStoreLog.Transport, topic);
        return Results.Accepted(value: new PublishResponse(message.MessageId, copies));
    }

    private static IResult TooLarge(int max) =>
        Results.Problem($"The message is larger than the {max}-byte limit.", statusCode: StatusCodes.Status413PayloadTooLarge);

    private static async Task<IResult> SubscribeAsync(string topic, string subscriberId, ITopicStore store, CancellationToken cancellationToken)
    {
        var expiresAt = await store.UpsertSubscriptionAsync(topic, subscriberId, cancellationToken);
        return Results.Ok(new SubscriptionResponse(topic, subscriberId, expiresAt));
    }

    private static async Task<IResult> GetSubscriptionAsync(string topic, string subscriberId, ITopicStore store, CancellationToken cancellationToken) =>
        await store.GetStatsAsync(topic, subscriberId, cancellationToken) is { } stats
            ? Results.Ok(new { topic, subscriberId, stats.ExpiresAt, stats.Pending, stats.DeadLettered })
            : Results.NotFound();

    private static async Task<IResult> UnsubscribeAsync(string topic, string subscriberId, ITopicStore store, CancellationToken cancellationToken) =>
        await store.RemoveSubscriptionAsync(topic, subscriberId, cancellationToken) ? Results.NoContent() : Results.NotFound();

    // Long-poll: returns as soon as there are messages, or an empty list once the wait is over.
    private static async Task<IResult> ReceiveAsync(string topic, string subscriberId, int? wait, int? max,
        ITopicStore store, IOptions<TopicServerOptions> options, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!await store.TouchSubscriptionAsync(topic, subscriberId, cancellationToken))
            return Results.NotFound();

        var waitFor = TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, (int)o.MaxWait.TotalSeconds));
        var take = Math.Clamp(max ?? 10, 1, o.MaxMessagesPerReceive);
        var deadline = Stopwatch.StartNew();

        while (true)
        {
            var claimed = await store.ClaimAsync(topic, subscriberId, take, cancellationToken);
            if (claimed.Count > 0 || deadline.Elapsed >= waitFor)
                return Results.Ok(claimed.Select(ToDto).ToList());

            var remaining = waitFor - deadline.Elapsed;
            try { await Task.Delay(remaining < o.PollInterval ? remaining : o.PollInterval, cancellationToken); }
            catch (OperationCanceledException) { return Results.Ok(Array.Empty<ReceivedMessageDto>()); }
        }
    }

    private static async Task<IResult> AckAsync(string topic, string subscriberId, string receipt, ITopicStore store,
        CancellationToken cancellationToken) =>
        ReceiptResult(await store.AckAsync(topic, subscriberId, receipt, cancellationToken));

    private static async Task<IResult> NackAsync(string topic, string subscriberId, string receipt, HttpRequest request,
        ITopicStore store, CancellationToken cancellationToken)
    {
        NackRequest? body = null;
        if (request.ContentLength > 0)
        {
            try { body = await request.ReadFromJsonAsync<NackRequest>(cancellationToken); }
            catch (JsonException) { return Results.Problem("The body must be JSON.", statusCode: StatusCodes.Status400BadRequest); }
        }
        var error = body?.Error is { Length: > 4000 } longError ? longError[..4000] : body?.Error;
        return ReceiptResult(await store.NackAsync(topic, subscriberId, receipt, error, cancellationToken));
    }

    private static IResult ReceiptResult(ReceiptOutcome outcome) => outcome switch
    {
        ReceiptOutcome.Done      => Results.NoContent(),
        ReceiptOutcome.LeaseLost => Results.Problem("The claim on this message has been lost; it may be delivered again.",
                                        statusCode: StatusCodes.Status410Gone),
        _                        => Results.Problem("Unknown receipt.", statusCode: StatusCodes.Status400BadRequest)
    };

    private static ReceivedMessageDto ToDto(TopicDelivery d)
    {
        using var document = JsonDocument.Parse(d.Payload);
        var traceParent = d.TraceContext?.Split(' ', 2)[0];
        return new ReceivedMessageDto(d.MessageId, d.Receipt, d.PublishedAt, d.Attempt, traceParent, document.RootElement.Clone());
    }

    private static string? CaptureTraceContext()
    {
        var activity = Activity.Current;
        if (activity == null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id == null) return null;
        return string.IsNullOrEmpty(activity.TraceStateString) ? activity.Id : $"{activity.Id} {activity.TraceStateString}";
    }
}

internal enum TopicAccess { Publish, Subscribe }

/// <summary>
/// Validates the topic and subscriber names, then requires the caller's token to grant
/// <c>publish:{topic}</c> or <c>subscribe:{topic}</c> (or the <c>*</c> wildcard).
/// </summary>
internal sealed class TopicAccessFilter(TopicAccess access) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var topic = http.GetRouteValue("topic") as string;
        var subscriberId = http.GetRouteValue("subscriberId") as string;
        if (!TopicNames.IsValid(topic) || (subscriberId != null && !TopicNames.IsValid(subscriberId)))
            return Results.Problem("Topic and subscriber names are 1-200 letters, digits, '.', '_' or '-', starting with a letter or digit.",
                statusCode: StatusCodes.Status400BadRequest);

        var claimType = http.RequestServices.GetRequiredService<IOptions<TopicServerOptions>>().Value.Jwt.TopicClaimType;
        if (!IsAllowed(http.User, claimType, access, topic!))
            return Results.Forbid();

        return await next(context);
    }

    internal static bool IsAllowed(ClaimsPrincipal user, string claimType, TopicAccess access, string topic)
    {
        var verb = access == TopicAccess.Publish ? "publish" : "subscribe";
        var exact = $"{verb}:{topic}";
        var any = $"{verb}:*";
        return user.FindAll(claimType)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(v => v == exact || v == any);
    }
}
