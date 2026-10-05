using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TownSuite.WorkQueues;

/// <summary>
/// The last delivery error recorded against a message. Stored as JSON in the <c>lasterror</c>
/// column (SQL transports) or the dead-letter entry (Redis) so a fault can be redelivered,
/// and listed, after the original exception is gone.
/// </summary>
internal sealed class StoredError
{
    [JsonPropertyName("type")]         public string Type { get; init; } = string.Empty;
    [JsonPropertyName("message")]      public string Message { get; init; } = string.Empty;
    [JsonPropertyName("stackTrace")]   public string? StackTrace { get; init; }
    [JsonPropertyName("nonRetryable")] public bool NonRetryable { get; init; }

    public static StoredError From(Exception ex, bool nonRetryable) => new()
    {
        Type         = ex.GetType().FullName ?? ex.GetType().Name,
        Message      = ex.Message,
        StackTrace   = ex.StackTrace,
        NonRetryable = nonRetryable
    };

    public string ToJson() => JsonSerializer.Serialize(this);

    public static StoredError? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<StoredError>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Everything needed to build a <see cref="Fault{T}"/> for one dead-lettered message.</summary>
internal sealed record FaultInfo(
    string Channel,
    string Payload,
    Guid MessageId,
    int AttemptCount,
    DateTimeOffset FaultedAt,
    StoredError? Error,
    bool IsRedelivery);

/// <summary>
/// Holds the <see cref="Fault{T}"/> consumers subscribed on one bus and delivers faults to them.
/// Shared by every transport.
/// </summary>
internal sealed class FaultRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<object, Func<FaultInfo, CancellationToken, Task>>> _handlers = new();

    /// <summary>Channels that have at least one fault consumer.</summary>
    public string[] Channels => _handlers.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key).ToArray();

    public bool HasConsumers(string channel) =>
        _handlers.TryGetValue(channel, out var handlers) && !handlers.IsEmpty;

    public void Add<T>(IConsumer<Fault<T>> consumer)
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        var handlers = _handlers.GetOrAdd(channel,
            _ => new ConcurrentDictionary<object, Func<FaultInfo, CancellationToken, Task>>());
        handlers.TryAdd(consumer, async (info, token) =>
        {
            if (LegacyJsonDeserializer.Deserialize(info.Payload, typeof(T)) is not T original) return;

            var fault = new Fault<T>
            {
                OriginalMessage  = original,
                ExceptionType    = info.Error?.Type ?? string.Empty,
                ExceptionMessage = info.Error?.Message ?? string.Empty,
                StackTrace       = info.Error?.StackTrace,
                FaultedAt        = info.FaultedAt,
                AttemptCount     = info.AttemptCount,
                MessageId        = info.MessageId,
                NonRetryable     = info.Error?.NonRetryable ?? false,
                IsRedelivery     = info.IsRedelivery
            };
            await consumer.Consume(new SimpleConsumeContext<Fault<T>>(fault, token));
        });
    }

    /// <summary>
    /// Delivers the fault to every consumer for its channel. Returns <see langword="true"/> when it
    /// was delivered (record it as done), <see langword="false"/> when this bus has no fault
    /// consumer for the channel (leave it for a bus that does). Throws if a consumer throws.
    /// </summary>
    public async Task<bool> DispatchAsync(FaultInfo info, CancellationToken token)
    {
        if (!_handlers.TryGetValue(info.Channel, out var handlers) || handlers.IsEmpty)
            return false;

        await Task.WhenAll(handlers.Values.Select(h => h(info, token)));
        return true;
    }

    /// <summary>Builds a listing entry from stored dead-letter fields.</summary>
    public static DeadLetteredMessage<T> ToDeadLettered<T>(Guid messageId, string payload, DateTimeOffset sentTime,
        DateTimeOffset failedAt, int attemptCount, string? lastError, bool faultDelivered)
    {
        var error = StoredError.Parse(lastError);
        T? message = default;
        try
        {
            if (LegacyJsonDeserializer.Deserialize(payload, typeof(T)) is T typed) message = typed;
        }
        catch (JsonException) { }

        return new DeadLetteredMessage<T>
        {
            MessageId        = messageId,
            Message          = message,
            Payload          = payload,
            SentTime         = sentTime,
            FailedAt         = failedAt,
            AttemptCount     = attemptCount,
            ExceptionType    = error?.Type,
            ExceptionMessage = error?.Message,
            StackTrace       = error?.StackTrace,
            NonRetryable     = error?.NonRetryable ?? false,
            FaultDelivered   = faultDelivered
        };
    }
}

/// <summary>
/// Lets one caller through per interval, across all of a bus's polling loops. Used so fault
/// redelivery is checked every few seconds instead of on every polling cycle.
/// </summary>
internal sealed class IntervalGate(TimeSpan interval)
{
    private long _nextTicks;

    public static IntervalGate ForFaultRedelivery(BatchOptions options) =>
        new(options.FaultRedeliveryDelay < TimeSpan.FromSeconds(5) ? options.FaultRedeliveryDelay : TimeSpan.FromSeconds(5));

    public bool TryEnter()
    {
        var now = DateTime.UtcNow.Ticks;
        var next = Interlocked.Read(ref _nextTicks);
        return now >= next && Interlocked.CompareExchange(ref _nextTicks, now + interval.Ticks, next) == next;
    }
}
