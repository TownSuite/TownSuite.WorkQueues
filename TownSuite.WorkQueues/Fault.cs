namespace TownSuite.WorkQueues;

/// <summary>
/// Describes a message that exhausted all delivery retries and was dead-lettered.
/// Register an <see cref="IConsumer{T}"/> where T is <c>Fault&lt;TMessage&gt;</c>
/// via <see cref="IMessageBus.SubscribeFault{T}"/> to receive a notification whenever
/// a message of type <typeparamref name="T"/> is dead-lettered.
/// </summary>
/// <remarks>
/// Fault delivery is at-least-once. The dead-letter is committed first; if the fault consumer
/// throws, or the process stops before it runs, the fault is delivered again after
/// <c>BatchOptions.FaultRedeliveryDelay</c>. Fault consumers must be idempotent.
/// </remarks>
/// <typeparam name="T">The original message type that failed delivery.</typeparam>
public sealed class Fault<T>
{
    /// <summary>The original message that could not be delivered.</summary>
    public required T OriginalMessage { get; init; }

    /// <summary>
    /// The fully-qualified exception type name from the last failed delivery attempt.
    /// For Redis transports this will be <c>System.InvalidOperationException</c> when the message
    /// was dead-lettered by the reclaim cycle, because the original exception is not retained
    /// across retry cycles.
    /// </summary>
    public required string ExceptionType { get; init; }

    /// <summary>The exception message from the last failed delivery attempt.</summary>
    public required string ExceptionMessage { get; init; }

    /// <summary>
    /// The stack trace from the last failed delivery attempt.
    /// <see langword="null"/> when the original exception was not retained (Redis reclaim cycle).
    /// </summary>
    public string? StackTrace { get; init; }

    /// <summary>UTC timestamp when the message was dead-lettered.</summary>
    public required DateTimeOffset FaultedAt { get; init; }

    /// <summary>Total number of delivery attempts made before dead-lettering.</summary>
    public required int AttemptCount { get; init; }

    /// <summary>
    /// The identifier of the dead-lettered message. Pass it to
    /// <see cref="IMessageBus.ReplayDeadLettered{T}(Guid, CancellationToken)"/> to replay just this message.
    /// <see cref="Guid.Empty"/> for messages published before message ids were recorded.
    /// </summary>
    public Guid MessageId { get; init; }

    /// <summary>
    /// <see langword="true"/> when the message was dead-lettered without exhausting
    /// <c>MaxRetries</c> because <c>BatchOptions.IsRetryable</c> returned <see langword="false"/>.
    /// </summary>
    public bool NonRetryable { get; init; }

    /// <summary>
    /// <see langword="true"/> when this fault is being delivered again because an earlier
    /// delivery attempt threw or did not complete.
    /// </summary>
    public bool IsRedelivery { get; init; }

    /// <summary>
    /// <see langword="true"/> when the message was dead-lettered without being delivered because
    /// its <see cref="PublishOptions.ExpiresAt"/> passed. <see cref="ExceptionType"/> is then
    /// <see cref="MessageExpiredException"/>.
    /// </summary>
    public bool Expired { get; init; }
}
