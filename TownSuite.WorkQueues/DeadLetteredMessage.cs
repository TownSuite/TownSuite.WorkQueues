namespace TownSuite.WorkQueues;

/// <summary>
/// A dead-lettered message returned by <see cref="IMessageBus.GetDeadLettered{T}"/>,
/// for admin screens and support tools that list failures before replaying them.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
public sealed class DeadLetteredMessage<T>
{
    /// <summary>
    /// The message identifier. Pass it to
    /// <see cref="IMessageBus.ReplayDeadLettered{T}(Guid, CancellationToken)"/> to replay it.
    /// <see cref="Guid.Empty"/> for messages published before message ids were recorded.
    /// </summary>
    public required Guid MessageId { get; init; }

    /// <summary>
    /// The deserialised message, or <see langword="default"/> when <see cref="Payload"/> no longer
    /// deserialises to <typeparamref name="T"/> (for example after an incompatible type change).
    /// </summary>
    public T? Message { get; init; }

    /// <summary>The raw JSON payload as stored.</summary>
    public required string Payload { get; init; }

    /// <summary>When the message was published (UTC).</summary>
    public required DateTimeOffset SentTime { get; init; }

    /// <summary>When the message was dead-lettered (UTC).</summary>
    public required DateTimeOffset FailedAt { get; init; }

    /// <summary>Delivery attempts made before dead-lettering.</summary>
    public required int AttemptCount { get; init; }

    /// <summary>
    /// Exception type from the last failed attempt, or <see langword="null"/> when it was not recorded
    /// (messages dead-lettered by earlier library versions).
    /// </summary>
    public string? ExceptionType { get; init; }

    /// <summary>Exception message from the last failed attempt, when recorded.</summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>Stack trace from the last failed attempt, when recorded.</summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// <see langword="true"/> when the message was dead-lettered because
    /// <c>BatchOptions.IsRetryable</c> returned <see langword="false"/>.
    /// </summary>
    public bool NonRetryable { get; init; }

    /// <summary>
    /// <see langword="true"/> once a <see cref="Fault{T}"/> for this dead-letter has been delivered
    /// to a fault consumer.
    /// </summary>
    public bool FaultDelivered { get; init; }
}
