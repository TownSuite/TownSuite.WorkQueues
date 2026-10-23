using System.Text.Json;
using System.Text.RegularExpressions;

namespace TownSuite.WorkQueues.Client;

/// <summary>Rules for topic and subscriber names, shared by the client and the server.</summary>
public static class TopicNames
{
    private static readonly Regex Valid = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// <see langword="true"/> when <paramref name="name"/> is 1–200 characters of letters, digits,
    /// <c>.</c>, <c>_</c> and <c>-</c>, starting with a letter or digit. Names are case-sensitive.
    /// </summary>
    public static bool IsValid(string? name) => name != null && Valid.IsMatch(name);

    internal static string Require(string name, string paramName) =>
        IsValid(name) ? name : throw new ArgumentException(
            $"'{name}' is not a valid name: use 1-200 letters, digits, '.', '_' or '-', starting with a letter or digit.", paramName);
}

/// <summary>Response to a publish.</summary>
/// <param name="MessageId">The id every copy of the message carries.</param>
/// <param name="Subscriptions">How many active subscriptions received a copy. <c>0</c> means nobody was subscribed and the message was dropped.</param>
public sealed record PublishResponse(Guid MessageId, int Subscriptions);

/// <summary>Response to registering or renewing a subscription.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="SubscriberId">The subscription's id.</param>
/// <param name="ExpiresAt">When the subscription expires unless it is renewed or polled before then.</param>
public sealed record SubscriptionResponse(string Topic, string SubscriberId, DateTimeOffset ExpiresAt);

/// <summary>A message as it is sent over the wire by the receive endpoint.</summary>
/// <param name="MessageId">The id the publisher was given.</param>
/// <param name="Receipt">Opaque handle for acknowledging this delivery.</param>
/// <param name="PublishedAt">When the message was published.</param>
/// <param name="Attempt">1 on first delivery, then 2, 3… for redeliveries.</param>
/// <param name="TraceParent">The publisher's W3C <c>traceparent</c>, if any.</param>
/// <param name="Payload">The JSON the publisher sent.</param>
public sealed record ReceivedMessageDto(Guid MessageId, string Receipt, DateTimeOffset PublishedAt, int Attempt,
    string? TraceParent, JsonElement Payload);

/// <summary>Body of a negative acknowledgement.</summary>
/// <param name="Error">Why the message could not be handled; kept with the message if it is dead-lettered.</param>
public sealed record NackRequest(string? Error);

/// <summary>A delivered message, deserialised.</summary>
/// <typeparam name="T">The message type.</typeparam>
public sealed class ReceivedMessage<T>
{
    internal ReceivedMessage(T message, ReceivedMessageDto dto, string topic, string subscriberId)
    {
        Message      = message;
        MessageId    = dto.MessageId;
        Receipt      = dto.Receipt;
        PublishedAt  = dto.PublishedAt;
        Attempt      = dto.Attempt;
        TraceParent  = dto.TraceParent;
        Topic        = topic;
        SubscriberId = subscriberId;
    }

    /// <summary>The message.</summary>
    public T Message { get; }

    /// <summary>The id the publisher was given; the same on every copy and every redelivery.</summary>
    public Guid MessageId { get; }

    /// <summary>Handle to pass to <see cref="WorkQueuesClient.AckAsync"/> or <see cref="WorkQueuesClient.NackAsync"/>.</summary>
    public string Receipt { get; }

    /// <summary>When the message was published.</summary>
    public DateTimeOffset PublishedAt { get; }

    /// <summary>1 on first delivery, then 2, 3… for redeliveries.</summary>
    public int Attempt { get; }

    /// <summary>The publisher's W3C <c>traceparent</c>, if any.</summary>
    public string? TraceParent { get; }

    /// <summary>The topic it was published to.</summary>
    public string Topic { get; }

    /// <summary>The subscription it was delivered to.</summary>
    public string SubscriberId { get; }
}

/// <summary>A request to the server failed.</summary>
public class WorkQueuesHttpException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WorkQueuesHttpException(System.Net.HttpStatusCode statusCode, string message) : base(message) =>
        StatusCode = statusCode;

    /// <summary>The HTTP status the server returned.</summary>
    public System.Net.HttpStatusCode StatusCode { get; }
}

/// <summary>
/// The subscription does not exist: it was never registered, was removed, or expired because it
/// was not polled within the server's idle timeout. Register it again with
/// <see cref="WorkQueuesClient.SubscribeAsync"/>.
/// </summary>
public sealed class SubscriptionNotFoundException : WorkQueuesHttpException
{
    /// <summary>Creates the exception.</summary>
    public SubscriptionNotFoundException(string topic, string subscriberId)
        : base(System.Net.HttpStatusCode.NotFound, $"Subscription '{subscriberId}' on topic '{topic}' does not exist or has expired.") { }
}
