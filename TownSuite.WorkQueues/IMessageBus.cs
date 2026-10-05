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
    /// Serialises <paramref name="message"/> and inserts it with per-message options such as a
    /// delivery time or an expiry.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="options">Delivery time and expiry for this message.</param>
    /// <param name="cancellationToken">Token to cancel the publish operation.</param>
    /// <returns>The <see cref="ConsumeContext{T}.MessageId"/> assigned to the message.</returns>
    Task<Guid> Publish<T>(T message, PublishOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support publish options.");

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
    /// when messages of type <typeparamref name="T"/> are dead-lettered.
    /// Delivery is at-least-once: a fault whose consumer throws, or whose process stops first,
    /// is delivered again after <c>BatchOptions.FaultRedeliveryDelay</c>, by this or any other bus
    /// on the same store that has a fault consumer for <typeparamref name="T"/>. A bus does not
    /// need to subscribe a consumer for <typeparamref name="T"/> itself to deliver its faults.
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
    /// <param name="options">Optional delivery time and expiry.</param>
    /// <param name="cancellationToken">Token to cancel the publish operation.</param>
    /// <returns>The <see cref="ConsumeContext{T}.MessageId"/> assigned to the message.</returns>
    Task<Guid> Publish<T>(T message, System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction, PublishOptions? options = null,
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
    /// Lists dead-lettered messages of type <typeparamref name="T"/>, newest first, with the last
    /// error recorded for each and whether its <see cref="Fault{T}"/> has been delivered.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="skip">Number of messages to skip, for paging.</param>
    /// <param name="take">Maximum number of messages to return.</param>
    /// <param name="cancellationToken">Token to cancel the query.</param>
    Task<IReadOnlyList<DeadLetteredMessage<T>>> GetDeadLettered<T>(int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support listing dead-lettered messages.");

    /// <summary>
    /// Deletes successfully processed messages (on every channel) processed before
    /// <paramref name="processedBefore"/>. Deletes in small batches so it can run against a busy
    /// queue. Run it on a schedule to keep the store from growing without bound.
    /// </summary>
    /// <param name="processedBefore">Only messages processed before this time are deleted.</param>
    /// <param name="cancellationToken">Token to cancel the purge between batches.</param>
    /// <returns>The number of messages deleted.</returns>
    Task<long> PurgeProcessed(DateTimeOffset processedBefore, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support purging.");

    /// <summary>
    /// Deletes dead-lettered messages of type <typeparamref name="T"/> that were dead-lettered
    /// before <paramref name="failedBefore"/>, including any fault not yet delivered for them.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="failedBefore">Only messages dead-lettered before this time are deleted.</param>
    /// <param name="cancellationToken">Token to cancel the purge between batches.</param>
    /// <returns>The number of messages deleted.</returns>
    Task<long> PurgeDeadLettered<T>(DateTimeOffset failedBefore, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support purging.");

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
