namespace TownSuite.WorkQueues.Redis;

/// <summary>
/// A simple Redis-backed FIFO work queue using Redis Lists (LPUSH / RPOP).
/// Unlike <see cref="IWorkQueue"/>, no database connection is required.
/// </summary>
/// <remarks>
/// There is no retry or dead-letter logic in this queue. Use <see cref="RedisMessageBus"/>
/// (via <see cref="IMessageBus"/>) when you need automatic retry and dead-lettering.
/// </remarks>
[Obsolete("Legacy Redis list queue without retry or dead-lettering. Use RedisMessageBus (IMessageBus). See MIGRATING.md.", DiagnosticId = "TSWQ001")]
public interface IRedisWorkQueue
{
    /// <summary>
    /// Serialises <paramref name="payload"/> and pushes it to the head of the named
    /// <paramref name="channel"/> list. Items are dequeued from the tail, preserving FIFO order.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="channel">Logical channel name. Must not exceed 500 characters.</param>
    /// <param name="payload">The object to enqueue.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task EnqueueAsync<T>(string channel, T payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pops and returns the next item from <paramref name="channel"/>,
    /// or <see langword="null"/> if the channel is empty.
    /// </summary>
    /// <typeparam name="T">The expected payload type.</typeparam>
    /// <param name="channel">Logical channel name.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The deserialised payload, or <see langword="null"/> when the channel is empty.</returns>
    Task<T?> DequeueAsync<T>(string channel, CancellationToken cancellationToken = default);
}
