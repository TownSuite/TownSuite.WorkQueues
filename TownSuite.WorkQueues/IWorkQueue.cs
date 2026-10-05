using System.Data;
using System.Data.Common;

namespace TownSuite.WorkQueues;

/// <summary>
/// Defines a SQL-backed work queue that supports enqueuing and dequeuing typed payloads.
/// Implementations use <c>FOR UPDATE SKIP LOCKED</c> (or an equivalent) so multiple concurrent
/// workers can safely dequeue from the same channel without blocking each other.
/// </summary>
[Obsolete("Legacy work queue API. Use IMessageBus: Publish/Subscribe, or Publish(message, connection, transaction) to enqueue inside your own transaction. See MIGRATING.md.", DiagnosticId = "TSWQ001")]
public interface IWorkQueue
{
    /// <summary>
    /// Serialises <paramref name="payload"/> as JSON and inserts it into the named channel.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="channel">Logical channel name. Must not exceed 500 characters.</param>
    /// <param name="payload">The object to enqueue.</param>
    /// <param name="con">An open (or auto-opened) database connection. Must be a <see cref="DbConnection"/>.</param>
    /// <param name="txn">Optional transaction. When <see langword="null"/> the insert is auto-committed.</param>
    /// <returns><see langword="true"/> if the row was inserted.</returns>
    Task<bool> Enqueue<T>(string channel, T payload, IDbConnection con, IDbTransaction? txn = null);

    /// <inheritdoc cref="Enqueue{T}(string,T,IDbConnection,IDbTransaction)"/>
    Task<bool> Enqueue<T>(string channel, T payload, DbConnection con, DbTransaction? txn = null);

    /// <summary>
    /// Dequeues the next available message from the channel.
    /// A transaction is required; commit it after successful processing or roll back to leave
    /// the message available for retry (destructive mode) or let the timeout expire (non-destructive mode).
    /// </summary>
    /// <typeparam name="T">The expected payload type.</typeparam>
    /// <param name="channel">Logical channel name.</param>
    /// <param name="con">An open database connection. Must be a <see cref="DbConnection"/>.</param>
    /// <param name="txn">
    /// Active transaction. Must not be <see langword="null"/> — pass one obtained from
    /// <c>cn.BeginTransaction()</c>. Throws <see cref="WorkQueuesException"/> if omitted.
    /// </param>
    /// <param name="offset">
    /// Number of rows to skip. Useful when multiple workers share a channel or when a known-bad
    /// message needs to be skipped temporarily.
    /// </param>
    /// <returns>The deserialised payload, or <see langword="default"/> when the queue is empty.</returns>
    [return: System.Diagnostics.CodeAnalysis.MaybeNull]
    Task<T> Dequeue<T>(string channel, IDbConnection con, IDbTransaction txn, int offset = 0);

    /// <inheritdoc cref="Dequeue{T}(string,IDbConnection,IDbTransaction,int)"/>
    [return: System.Diagnostics.CodeAnalysis.MaybeNull]
    Task<T> Dequeue<T>(string channel, DbConnection con, DbTransaction txn, int offset = 0);
}
