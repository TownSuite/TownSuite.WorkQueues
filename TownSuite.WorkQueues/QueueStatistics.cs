namespace TownSuite.WorkQueues;

/// <summary>
/// A point-in-time snapshot of one message bus channel, returned by
/// <see cref="IMessageBus.GetQueueStatistics{T}"/>. Use it for health checks and
/// for alerting on backlog growth or dead-letters.
/// </summary>
public sealed class QueueStatistics
{
    /// <summary>The channel name (<c>typeof(T).FullName</c>).</summary>
    public required string Channel { get; init; }

    /// <summary>
    /// Messages not yet processed and not dead-lettered, including messages scheduled
    /// for later delivery and messages waiting for a retry delay to elapse.
    /// </summary>
    public required long PendingCount { get; init; }

    /// <summary>Dead-lettered messages still held for inspection or replay.</summary>
    public required long DeadLetteredCount { get; init; }

    /// <summary>
    /// The earliest time a currently deliverable message became ready: its scheduled
    /// delivery time if it has one, otherwise its publish time. <see langword="null"/>
    /// when nothing is waiting to be delivered.
    /// </summary>
    public DateTimeOffset? OldestReadySince { get; init; }

    /// <summary>When this snapshot was taken (UTC).</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// How long the oldest deliverable message has been waiting, or <see langword="null"/>
    /// when nothing is waiting. A steadily growing value means consumers are not keeping up
    /// or no consumer is subscribed to the channel.
    /// </summary>
    public TimeSpan? OldestReadyAge => OldestReadySince is { } since
        ? (CapturedAt > since ? CapturedAt - since : TimeSpan.Zero)
        : null;
}
