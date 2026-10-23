using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TownSuite.WorkQueues.Client;

/// <summary>Options for <see cref="WorkQueuesClient"/>.</summary>
public sealed class WorkQueuesClientOptions
{
    /// <summary>
    /// Returns the bearer token to send with each request, or <see langword="null"/> to send none.
    /// Called before every request, so a provider that caches and refreshes tokens works as is.
    /// </summary>
    public Func<CancellationToken, ValueTask<string?>>? AccessTokenProvider { get; set; }

    /// <summary>How long a receive waits on the server for a message to arrive. Defaults to 30 seconds.</summary>
    public TimeSpan LongPollWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Most messages returned by one receive. Defaults to 10.</summary>
    public int MaxMessages { get; set; } = 10;

    /// <summary>First wait before retrying after the server cannot be reached. Doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between reconnect attempts. Defaults to 30 seconds.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Serializer options for message payloads. Defaults to <see cref="JsonSerializerDefaults.Web"/>.</summary>
    public JsonSerializerOptions JsonOptions { get; set; } = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Client for <c>TownSuite.WorkQueues.Server</c>: publish to topics and subscribe from any data
/// centre over HTTPS. Subscribers make only outgoing requests.
/// </summary>
/// <remarks>
/// <para>Give it an <see cref="HttpClient"/> whose <see cref="HttpClient.BaseAddress"/> is the server
/// (for example from <c>IHttpClientFactory</c>). Keep <see cref="HttpClient.Timeout"/> above
/// <see cref="WorkQueuesClientOptions.LongPollWait"/>; the default 100 seconds is fine.</para>
/// <para>Delivery is at least once. A message that is not acknowledged within the server's claim
/// lease is delivered again, so handlers must be idempotent (use <see cref="ReceivedMessage{T}.MessageId"/>).</para>
/// </remarks>
public sealed class WorkQueuesClient
{
    /// <summary>The activity source the subscription loop starts its consume spans on.</summary>
    public const string ActivitySourceName = "TownSuite.WorkQueues.Client";

    private static readonly ActivitySource Source = new(ActivitySourceName,
        typeof(WorkQueuesClient).Assembly.GetName().Version?.ToString());

    private readonly HttpClient _http;
    private readonly WorkQueuesClientOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates a client over <paramref name="httpClient"/>.</summary>
    public WorkQueuesClient(HttpClient httpClient, WorkQueuesClientOptions? options = null, ILogger<WorkQueuesClient>? logger = null)
    {
        _http    = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new WorkQueuesClientOptions();
        _logger  = (ILogger?)logger ?? NullLogger.Instance;
    }

    // ── Publishing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes <paramref name="message"/> to <paramref name="topic"/>. Every subscription active
    /// on the topic gets its own copy; with no subscriptions the message is dropped.
    /// </summary>
    public async Task<PublishResponse> PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        TopicNames.Require(topic, nameof(topic));
        using var request = await CreateRequestAsync(HttpMethod.Post, $"api/v1/topics/{topic}/messages", cancellationToken);
        request.Content = JsonContent.Create(message, options: _options.JsonOptions);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, topic, null, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PublishResponse>(_options.JsonOptions, cancellationToken))!;
    }

    // ── Subscriptions ───────────────────────────────────────────────────────

    /// <summary>
    /// Registers or renews the subscription <paramref name="subscriberId"/> on <paramref name="topic"/>.
    /// It receives a copy of every message published from now on. Receiving also renews it, so a
    /// subscription that keeps polling never expires.
    /// </summary>
    /// <remarks>Processes that share a <paramref name="subscriberId"/> share its messages (each copy goes to one of them).</remarks>
    public async Task<SubscriptionResponse> SubscribeAsync(string topic, string subscriberId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, SubscriptionPath(topic, subscriberId), cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, topic, subscriberId, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(_options.JsonOptions, cancellationToken))!;
    }

    /// <summary>Removes the subscription and the messages waiting for it.</summary>
    public async Task UnsubscribeAsync(string topic, string subscriberId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, SubscriptionPath(topic, subscriberId), cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, topic, subscriberId, cancellationToken);
    }

    // ── Receiving ───────────────────────────────────────────────────────────

    /// <summary>
    /// Waits up to <paramref name="wait"/> (default <see cref="WorkQueuesClientOptions.LongPollWait"/>)
    /// for messages and returns as soon as there are any. Each must be acknowledged with
    /// <see cref="AckAsync"/> within the server's claim lease, or it is delivered again.
    /// </summary>
    /// <exception cref="SubscriptionNotFoundException">The subscription does not exist or has expired.</exception>
    public async Task<IReadOnlyList<ReceivedMessage<T>>> ReceiveAsync<T>(string topic, string subscriberId,
        TimeSpan? wait = null, int? maxMessages = null, CancellationToken cancellationToken = default)
    {
        var waitSeconds = (int)Math.Ceiling((wait ?? _options.LongPollWait).TotalSeconds);
        var max = maxMessages ?? _options.MaxMessages;
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"{SubscriptionPath(topic, subscriberId)}/messages?wait={waitSeconds}&max={max}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, topic, subscriberId, cancellationToken);

        var dtos = await response.Content.ReadFromJsonAsync<List<ReceivedMessageDto>>(_options.JsonOptions, cancellationToken) ?? new();
        return dtos.Select(d => new ReceivedMessage<T>(d.Payload.Deserialize<T>(_options.JsonOptions)!, d, topic, subscriberId)).ToList();
    }

    /// <summary>
    /// Acknowledges a delivery, so it is not delivered again. Returns <see langword="false"/> when the
    /// claim lease had already expired: the message may then be delivered again.
    /// </summary>
    public async Task<bool> AckAsync(string topic, string subscriberId, string receipt, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"{SubscriptionPath(topic, subscriberId)}/messages/{Uri.EscapeDataString(receipt)}/ack", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone) return false;
        await EnsureSuccessAsync(response, topic, subscriberId, cancellationToken);
        return true;
    }

    /// <summary>
    /// Reports that a delivery failed, so it is retried (after the server's retry delay) instead of
    /// waiting for its lease to expire. It still counts as an attempt towards dead-lettering.
    /// </summary>
    public async Task NackAsync(string topic, string subscriberId, string receipt, string? error = null,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"{SubscriptionPath(topic, subscriberId)}/messages/{Uri.EscapeDataString(receipt)}/nack", cancellationToken);
        request.Content = JsonContent.Create(new NackRequest(error), options: _options.JsonOptions);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone) return;
        await EnsureSuccessAsync(response, topic, subscriberId, cancellationToken);
    }

    /// <summary>
    /// Registers the subscription, then receives and handles messages until
    /// <paramref name="cancellationToken"/> is cancelled. A message is acknowledged when
    /// <paramref name="handler"/> returns, and negatively acknowledged when it throws. When the server
    /// cannot be reached the loop retries with backoff; when the subscription has expired it registers
    /// it again.
    /// </summary>
    /// <param name="topic">The topic.</param>
    /// <param name="subscriberId">The subscription id. Replicas of one service should share it.</param>
    /// <param name="handler">Handles one message. It is given <paramref name="cancellationToken"/>.</param>
    /// <param name="unsubscribeOnStop">
    /// Remove the subscription when the loop stops. Leave <see langword="false"/> (the default) for a
    /// service that will come back and wants the messages published meanwhile; the subscription
    /// expires on its own if it does not.
    /// </param>
    /// <param name="cancellationToken">Stops the loop.</param>
    public async Task RunSubscriptionAsync<T>(string topic, string subscriberId,
        Func<ReceivedMessage<T>, CancellationToken, Task> handler, bool unsubscribeOnStop = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var delay = _options.ReconnectDelay;
        bool registered = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!registered)
                {
                    await SubscribeAsync(topic, subscriberId, cancellationToken);
                    registered = true;
                }

                var messages = await ReceiveAsync<T>(topic, subscriberId, cancellationToken: cancellationToken);
                delay = _options.ReconnectDelay;

                foreach (var message in messages)
                    await HandleAsync(message, handler, cancellationToken);
            }
            catch (SubscriptionNotFoundException)
            {
                _logger.LogInformation("Subscription {SubscriberId} on topic {Topic} expired; registering it again", subscriberId, topic);
                registered = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Receiving from topic {Topic} as {SubscriberId} failed; retrying in {Delay}", topic, subscriberId, delay);
                registered = false;
                try { await Task.Delay(delay, cancellationToken); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
            }
        }

        if (unsubscribeOnStop)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await UnsubscribeAsync(topic, subscriberId, timeout.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove subscription {SubscriberId} on topic {Topic}; it expires on its own", subscriberId, topic);
            }
        }
    }

    private async Task HandleAsync<T>(ReceivedMessage<T> message, Func<ReceivedMessage<T>, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ActivityContext.TryParse(message.TraceParent, null, isRemote: true, out var parent);
        using var activity = Source.StartActivity($"process {message.Topic}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "townsuite.workqueues.http");
        activity?.SetTag("messaging.destination.name", message.Topic);
        activity?.SetTag("messaging.consumer.group.name", message.SubscriberId);
        activity?.SetTag("messaging.message.id", message.MessageId.ToString());

        try
        {
            await handler(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: leave the message for its lease to expire, so it is redelivered.
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Handler failed for message {MessageId} on topic {Topic}", message.MessageId, message.Topic);
            await NackAsync(message.Topic, message.SubscriberId, message.Receipt, $"{ex.GetType().FullName}: {ex.Message}", cancellationToken);
            return;
        }

        if (!await AckAsync(message.Topic, message.SubscriberId, message.Receipt, cancellationToken))
            _logger.LogWarning("Lease on message {MessageId} expired before it was acknowledged; it may be delivered again", message.MessageId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string SubscriptionPath(string topic, string subscriberId) =>
        $"api/v1/topics/{TopicNames.Require(topic, nameof(topic))}/subscriptions/{TopicNames.Require(subscriberId, nameof(subscriberId))}";

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path);
        if (_options.AccessTokenProvider != null && await _options.AccessTokenProvider(cancellationToken) is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string topic, string? subscriberId,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.NotFound && subscriberId != null)
            throw new SubscriptionNotFoundException(topic, subscriberId);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new WorkQueuesHttpException(response.StatusCode,
            $"{(int)response.StatusCode} {response.ReasonPhrase} from {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
    }
}
