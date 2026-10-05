namespace TownSuite.WorkQueues;

/// <summary>
/// A lightweight publish/subscribe message bus.
/// Messages are persisted to the backing store and dispatched asynchronously
/// by a background polling loop. Each subscriber receives every message published
/// to its registered type.
/// </summary>
/// <remarks>
/// <para><strong>Delivery guarantee:</strong> at-least-once. A message is retried up to
/// <c>BatchOptions.MaxRetries</c> times on failure, then dead-lettered.</para>
/// <para><strong>Multiple consumers on the same type:</strong> all registered consumers receive
/// the same message in a single dispatch. If any consumer throws, the entire message is retried —
/// including consumers that succeeded in the previous attempt. Design consumers to be idempotent.</para>
/// <para><strong>Lifecycle:</strong> dispose the bus (via <see cref="IAsyncDisposable.DisposeAsync"/>)
/// to stop the polling loop gracefully before application shutdown.</para>
/// </remarks>
public interface IMessageBus : IAsyncDisposable
{
    /// <summary>
    /// Serialises <paramref name="message"/> and inserts it into the backing store.
    /// Delivery to subscribers happens asynchronously on the next polling cycle.
    /// </summary>
    /// <typeparam name="T">The message type. <c>typeof(T).FullName</c> is used as the channel name
    /// and must not exceed 500 characters.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">Token to cancel the publish operation.</param>
    Task Publish<T>(T message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serialises <paramref name="message"/> and schedules it for delivery no earlier than
    /// <paramref name="deliverAfter"/>. The message will not be dispatched to consumers until
    /// the current time exceeds <paramref name="deliverAfter"/>.
    /// Transports that do not support scheduled delivery throw <see cref="NotSupportedException"/>.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="deliverAfter">Earliest time the message may be delivered.</param>
    /// <param name="cancellationToken">Token to cancel the publish operation.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown by the default implementation. Override in transports that support scheduled delivery.
    /// </exception>
    Task Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support scheduled delivery. " +
            "Use PostgresMessageBus, SqlServerMessageBus, or SqliteMessageBus.");

    /// <summary>
    /// Registers <paramref name="consumer"/> to receive messages of type <typeparamref name="T"/>.
    /// Multiple consumers may be subscribed to the same type; each receives every message.
    /// Subscribe before the bus starts delivering — call this immediately after construction
    /// and before the first <see cref="Publish{T}(T,CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="T">The message type to subscribe to.</typeparam>
    /// <param name="consumer">The consumer implementation.</param>
    void Subscribe<T>(IConsumer<T> consumer);

    /// <summary>
    /// Registers <paramref name="consumer"/> to receive <see cref="Fault{T}"/> notifications
    /// when messages of type <typeparamref name="T"/> are dead-lettered after exhausting retries.
    /// The default no-op implementation is a no-op; override in transports that support fault routing.
    /// </summary>
    /// <typeparam name="T">The original message type whose dead-lettering should be observed.</typeparam>
    /// <param name="consumer">The fault consumer implementation.</param>
    void SubscribeFault<T>(IConsumer<Fault<T>> consumer) { }

    /// <summary>
    /// Resets all dead-lettered messages of type <typeparamref name="T"/> so they will be
    /// redelivered on the next polling cycle. Returns the number of messages replayed.
    /// </summary>
    /// <typeparam name="T">The message type whose dead-letter queue should be replayed.</typeparam>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Serialises <paramref name="message"/> and inserts it using the caller's
    /// <paramref name="connection"/> and <paramref name="transaction"/>, so the message is only
    /// published if the caller's transaction commits (the transactional outbox pattern).
    /// Transports without a relational store throw <see cref="NotSupportedException"/>.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="connection">An open connection to the bus's database.</param>
    /// <param name="transaction">The caller's transaction, or <see langword="null"/> to auto-commit.</param>
    /// <param name="deliverAfter">Optional earliest delivery time.</param>
    /// <param name="cancellationToken">Token to cancel the publish operation.</param>
    /// <returns>The <see cref="ConsumeContext{T}.MessageId"/> assigned to the message.</returns>
    Task<Guid> Publish<T>(T message, System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction, DateTimeOffset? deliverAfter = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support transactional publish. " +
            "Use PostgresMessageBus, SqlServerMessageBus, or SqliteMessageBus.");

    /// <summary>
    /// Resets one dead-lettered message of type <typeparamref name="T"/>, identified by its
    /// <see cref="ConsumeContext{T}.MessageId"/>, so it is redelivered on the next polling cycle.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="messageId">The message identifier (also available as <c>Fault&lt;T&gt;.MessageId</c>).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns><see langword="true"/> if a dead-lettered message was found and replayed.</returns>
    Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support replaying a single message.");

    /// <summary>
    /// Returns pending and dead-letter counts and the age of the oldest waiting message for
    /// the channel of type <typeparamref name="T"/>. Intended for health checks and metrics.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="cancellationToken">Token to cancel the query.</param>
    Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support queue statistics.");

    /// <summary>
    /// <see langword="true"/> while every background polling loop is running.
    /// Use this to implement health checks: a <see langword="false"/> value after startup
    /// indicates the loop has stopped unexpectedly and the bus is no longer processing messages.
    /// </summary>
    bool IsPolling { get; }
}
