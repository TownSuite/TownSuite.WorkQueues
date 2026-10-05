using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace TownSuite.WorkQueues.Sqlite;

/// <summary>
/// SQLite-backed message bus with at-least-once delivery, automatic retry, and dead-lettering.
/// Designed for local development scenarios where multiple processes on the same machine
/// share a single SQLite file.
/// </summary>
/// <remarks>
/// <para>
/// SQLite does not support <c>FOR UPDATE SKIP LOCKED</c>. Instead, claiming is emulated with
/// a <c>lockeduntil</c> / <c>locktoken</c> column pair. A single atomic UPDATE claims a batch
/// of rows; concurrent processes skip any row whose <c>lockeduntil</c> has not yet expired.
/// SQLite's single-writer serialization makes this race-free.
/// </para>
/// <para>
/// <strong>Crash recovery:</strong> if a process dies while processing a claimed message,
/// the row becomes available again once the lease elapses
/// (<see cref="BatchOptions.ClaimLease"/>, or <see cref="SqliteTransportOptions.LockTimeout"/> when
/// that is not set) — unlike the default Postgres/SQL Server mode where a transaction rollback
/// makes the row immediately available. Set it to comfortably exceed the longest expected
/// consumer processing time.
/// </para>
/// <para>
/// WAL mode is enabled by <see cref="SqliteMigrationHostedService"/> on first startup.
/// Without it, concurrent reads and writes from separate processes would serialize
/// on file-level locks, causing unnecessary <c>SQLITE_BUSY</c> errors.
/// </para>
/// </remarks>
public class SqliteMessageBus : MessageBusBase
{
    private const int PurgeBatchSize = 5000;
    private readonly SqliteTransportOptions _options;

    public SqliteMessageBus(SqliteTransportOptions options, ILogger logger, IServiceProvider? serviceProvider = null)
        : base(options, logger, serviceProvider, "sqlite")
    {
        _options = options;
        StartPolling();
    }

    private TimeSpan Lease => _options.ClaimLease ?? _options.LockTimeout;

    // ── Publishing ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="connection"/> must be a <see cref="SqliteConnection"/> to the file the bus
    /// polls and <paramref name="transaction"/> (when supplied) a <see cref="SqliteTransaction"/> on it.
    /// </remarks>
    public override async Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection sqliteConnection)
            throw new ArgumentException("connection must be a SqliteConnection", nameof(connection));
        if (transaction != null && transaction is not SqliteTransaction)
            throw new ArgumentException("transaction must be a SqliteTransaction", nameof(transaction));

        if (sqliteConnection.State == ConnectionState.Closed)
            await sqliteConnection.OpenAsync(cancellationToken);

        var channel = ChannelName<T>();
        var messageId = Guid.NewGuid();
        await InsertAsync(sqliteConnection, transaction as SqliteTransaction, channel, JsonSerializer.Serialize(message),
            messageId, options ?? new PublishOptions(), cancellationToken);
        RecordPublished(channel);
        return messageId;
    }

    private protected override async Task InsertAsync(string channel, string payload, Guid messageId,
        PublishOptions options, CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await InsertAsync(conn, null, channel, payload, messageId, options, cancellationToken);
    }

    private static async Task InsertAsync(SqliteConnection conn, SqliteTransaction? tran, string channel, string payload,
        Guid messageId, PublishOptions options, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = """
            INSERT INTO workqueue(channel, payload, messageid, scheduledfor, expiresat)
            VALUES(@channel, @payload, @messageid, @scheduledfor, @expiresat)
            """;
        cmd.Parameters.AddWithValue("@channel",      channel);
        cmd.Parameters.AddWithValue("@payload",      payload);
        cmd.Parameters.AddWithValue("@messageid",    messageId.ToString());
        cmd.Parameters.AddWithValue("@scheduledfor", DateValue(options.DeliverAfter?.UtcDateTime));
        cmd.Parameters.AddWithValue("@expiresat",    DateValue(options.ExpiresAt?.UtcDateTime));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ── Claiming ────────────────────────────────────────────────────────────

    private protected override async Task<ClaimedBatch> ClaimAsync(string[] channels, int maxMessages, CancellationToken cancellationToken)
    {
        var lockToken  = Guid.NewGuid().ToString();
        var now        = ToSqliteDateTime(DateTime.UtcNow);
        var inClause   = string.Join(", ", channels.Select((_, i) => $"@ch{i}"));

        var conn = await OpenConnectionAsync(cancellationToken);
        try
        {
            // A single atomic UPDATE claims the batch. SQLite serializes writes across
            // processes, so only one winner sets locktoken; other pollers see lockeduntil
            // already in the future and skip those rows.
            await using (var claimCmd = conn.CreateCommand())
            {
                claimCmd.CommandText = $"""
                    UPDATE workqueue
                    SET lockeduntil = @lockeduntil, locktoken = @locktoken
                    WHERE id IN (
                        SELECT id FROM workqueue
                        WHERE timeprocessedutc IS NULL
                          AND failedat IS NULL
                          AND (lockeduntil IS NULL OR lockeduntil < @now)
                          AND (scheduledfor IS NULL OR scheduledfor <= @now)
                          AND channel IN ({inClause})
                        ORDER BY timecreatedutc
                        LIMIT @maxMessages
                    )
                    """;
                claimCmd.Parameters.AddWithValue("@lockeduntil", ToSqliteDateTime(DateTime.UtcNow.Add(Lease)));
                claimCmd.Parameters.AddWithValue("@locktoken",   lockToken);
                claimCmd.Parameters.AddWithValue("@now",         now);
                claimCmd.Parameters.AddWithValue("@maxMessages", maxMessages);
                for (int i = 0; i < channels.Length; i++)
                    claimCmd.Parameters.AddWithValue($"@ch{i}", channels[i]);

                await claimCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var messages = new List<MessageDto>();
            await using (var fetchCmd = conn.CreateCommand())
            {
                fetchCmd.CommandText = """
                    SELECT id, channel, payload, retrycount, messageid, timecreatedutc, expiresat
                    FROM workqueue
                    WHERE locktoken = @locktoken
                    ORDER BY timecreatedutc
                    """;
                fetchCmd.Parameters.AddWithValue("@locktoken", lockToken);

                await using var reader = await fetchCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    messages.Add(new MessageDto
                    {
                        Id             = reader.GetInt32(0),
                        Channel        = reader.GetString(1),
                        Payload        = reader.GetString(2),
                        RetryCount     = reader.GetInt32(3),
                        MessageId      = Guid.Parse(reader.GetString(4)),
                        TimeCreatedUtc = ParseSqliteDateTime(reader.GetString(5)).UtcDateTime,
                        ExpiresAtUtc   = reader.IsDBNull(6) ? null : ParseSqliteDateTime(reader.GetString(6)).UtcDateTime
                    });
                }
            }

            return new Batch(this, conn, lockToken, messages);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class Batch : ClaimedBatch
    {
        private readonly SqliteMessageBus _bus;
        private readonly SqliteConnection _conn;
        private readonly string _lockToken;

        public Batch(SqliteMessageBus bus, SqliteConnection conn, string lockToken, List<MessageDto> messages)
        {
            _bus = bus;
            _conn = conn;
            _lockToken = lockToken;
            Messages = messages;
        }

        public override async Task CompleteAsync(MessageDto message)
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE workqueue
                SET timeprocessedutc = @now, lockeduntil = NULL, locktoken = NULL
                WHERE id = @id AND locktoken = @locktoken
                """;
            cmd.Parameters.AddWithValue("@now", ToSqliteDateTime(DateTime.UtcNow));
            await ExecuteAsync(cmd, message);
        }

        public override async Task FailAsync(MessageDto message, FailureOutcome outcome)
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE workqueue
                SET retrycount        = @attempt,
                    failedat          = @failedat,
                    faultdispatchedat = NULL,
                    scheduledfor      = @scheduledfor,
                    lasterror         = @lasterror,
                    lockeduntil       = NULL,
                    locktoken         = NULL
                WHERE id = @id AND locktoken = @locktoken
                """;
            cmd.Parameters.AddWithValue("@attempt",      outcome.Attempt);
            cmd.Parameters.AddWithValue("@failedat",     outcome.DeadLetter ? ToSqliteDateTime(DateTime.UtcNow) : DBNull.Value);
            cmd.Parameters.AddWithValue("@scheduledfor", DateValue(outcome.ScheduledForUtc));
            cmd.Parameters.AddWithValue("@lasterror",    outcome.Error.ToJson());
            await ExecuteAsync(cmd, message);
        }

        private async Task ExecuteAsync(SqliteCommand cmd, MessageDto message)
        {
            cmd.Parameters.AddWithValue("@id",        message.Id);
            cmd.Parameters.AddWithValue("@locktoken", _lockToken);

            if (await cmd.ExecuteNonQueryAsync() == 0)
                _bus.Logger.LogWarning(
                    "Lease on message {MessageId} expired before its outcome was recorded; it may be delivered again. Increase the lease.",
                    message.MessageId);
        }

        public override ValueTask DisposeAsync() => _conn.DisposeAsync();
    }

    // ── Faults ──────────────────────────────────────────────────────────────

    // The claim pushes scheduledfor forward by the lease, so other processes skip the row
    // until it passes.
    private protected override async Task<IReadOnlyList<FaultInfo>> ClaimDueFaultsAsync(string[] channels, int maxFaults,
        TimeSpan lease, CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid().ToString();
        var now        = DateTime.UtcNow;
        var inClause   = string.Join(", ", channels.Select((_, i) => $"@ch{i}"));

        var due = new List<FaultInfo>();
        await using var conn = await OpenConnectionAsync(cancellationToken);

        await using (var claimCmd = conn.CreateCommand())
        {
            claimCmd.CommandText = $"""
                UPDATE workqueue
                SET scheduledfor = @leaseUntil, locktoken = @locktoken
                WHERE id IN (
                    SELECT id FROM workqueue
                    WHERE failedat IS NOT NULL
                      AND faultdispatchedat IS NULL
                      AND (scheduledfor IS NULL OR scheduledfor <= @now)
                      AND channel IN ({inClause})
                    ORDER BY failedat
                    LIMIT @maxFaults
                )
                """;
            claimCmd.Parameters.AddWithValue("@leaseUntil", ToSqliteDateTime(now.Add(lease)));
            claimCmd.Parameters.AddWithValue("@locktoken",  leaseToken);
            claimCmd.Parameters.AddWithValue("@now",        ToSqliteDateTime(now));
            claimCmd.Parameters.AddWithValue("@maxFaults",  maxFaults);
            for (int i = 0; i < channels.Length; i++)
                claimCmd.Parameters.AddWithValue($"@ch{i}", channels[i]);
            if (await claimCmd.ExecuteNonQueryAsync(cancellationToken) == 0) return due;
        }

        await using (var fetchCmd = conn.CreateCommand())
        {
            fetchCmd.CommandText = """
                SELECT channel, payload, messageid, retrycount, failedat, lasterror
                FROM workqueue WHERE locktoken = @locktoken AND failedat IS NOT NULL
                """;
            fetchCmd.Parameters.AddWithValue("@locktoken", leaseToken);
            await using var reader = await fetchCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(new FaultInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    Guid.Parse(reader.GetString(2)),
                    reader.GetInt32(3),
                    ParseSqliteDateTime(reader.GetString(4)),
                    StoredError.Parse(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    IsRedelivery: true));
            }
        }

        await using (var releaseCmd = conn.CreateCommand())
        {
            releaseCmd.CommandText = "UPDATE workqueue SET locktoken = NULL WHERE locktoken = @locktoken AND failedat IS NOT NULL";
            releaseCmd.Parameters.AddWithValue("@locktoken", leaseToken);
            await releaseCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return due;
    }

    private protected override async Task MarkFaultDeliveredAsync(FaultInfo fault)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workqueue SET faultdispatchedat = @now
            WHERE channel = @channel AND messageid = @messageid AND failedat IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@now",       ToSqliteDateTime(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@channel",   fault.Channel);
        cmd.Parameters.AddWithValue("@messageid", fault.MessageId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Dead-letters, statistics and purging ────────────────────────────────

    private const string ReplaySet = """
        failedat = NULL, retrycount = 0, scheduledfor = NULL, faultdispatchedat = NULL,
        expiresat = NULL, lockeduntil = NULL, locktoken = NULL
        """;

    /// <inheritdoc />
    public override async Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"UPDATE workqueue SET {ReplaySet} WHERE channel = @channel AND failedat IS NOT NULL";
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE workqueue SET {ReplaySet}
            WHERE channel = @channel AND messageid = @messageid AND failedat IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@channel",   ChannelName<T>());
        cmd.Parameters.AddWithValue("@messageid", messageId.ToString());
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public override async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
    {
        var channel = ChannelName<T>();
        var now     = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM workqueue
                 WHERE channel = @channel AND timeprocessedutc IS NULL AND failedat IS NULL),
                (SELECT COUNT(*) FROM workqueue
                 WHERE channel = @channel AND failedat IS NOT NULL),
                (SELECT COUNT(*) FROM workqueue
                 WHERE channel = @channel AND failedat IS NOT NULL AND faultdispatchedat IS NULL),
                (SELECT MIN(COALESCE(scheduledfor, timecreatedutc)) FROM workqueue
                 WHERE channel = @channel AND timeprocessedutc IS NULL AND failedat IS NULL
                   AND (scheduledfor IS NULL OR scheduledfor <= @now))
            """;
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@now",     ToSqliteDateTime(now));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new QueueStatistics
        {
            Channel           = channel,
            PendingCount      = reader.GetInt64(0),
            DeadLetteredCount = reader.GetInt64(1),
            PendingFaultCount = reader.GetInt64(2),
            OldestReadySince  = reader.IsDBNull(3) ? null : ParseSqliteDateTime(reader.GetString(3)),
            CapturedAt        = new DateTimeOffset(now, TimeSpan.Zero)
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<DeadLetteredMessage<T>>> GetDeadLettered<T>(int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT messageid, payload, timecreatedutc, failedat, retrycount, lasterror, faultdispatchedat
            FROM workqueue
            WHERE channel = @channel AND failedat IS NOT NULL
            ORDER BY failedat DESC, id DESC
            LIMIT @take OFFSET @skip
            """;
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        cmd.Parameters.AddWithValue("@skip",    Math.Max(0, skip));
        cmd.Parameters.AddWithValue("@take",    Math.Max(0, take));

        var result = new List<DeadLetteredMessage<T>>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(FaultRegistry.ToDeadLettered<T>(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseSqliteDateTime(reader.GetString(2)),
                ParseSqliteDateTime(reader.GetString(3)),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                !reader.IsDBNull(6)));
        }
        return result;
    }

    /// <inheritdoc />
    public override Task<long> PurgeProcessed(DateTimeOffset processedBefore, CancellationToken cancellationToken = default) =>
        PurgeAsync($"""
            DELETE FROM workqueue WHERE id IN (
                SELECT id FROM workqueue
                WHERE timeprocessedutc IS NOT NULL AND timeprocessedutc < @before
                LIMIT {PurgeBatchSize})
            """, processedBefore, null, cancellationToken);

    /// <inheritdoc />
    public override Task<long> PurgeDeadLettered<T>(DateTimeOffset failedBefore, CancellationToken cancellationToken = default) =>
        PurgeAsync($"""
            DELETE FROM workqueue WHERE id IN (
                SELECT id FROM workqueue
                WHERE channel = @channel AND failedat IS NOT NULL AND failedat < @before
                LIMIT {PurgeBatchSize})
            """, failedBefore, ChannelName<T>(), cancellationToken);

    private async Task<long> PurgeAsync(string sql, DateTimeOffset before, string? channel, CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@before", ToSqliteDateTime(before.UtcDateTime));
            if (channel != null) cmd.Parameters.AddWithValue("@channel", channel);

            int deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < PurgeBatchSize) return total;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // Opens a connection and sets busy_timeout so concurrent writes retry rather than
    // failing immediately with SQLITE_BUSY.
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new SqliteConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return conn;
    }

    private static object DateValue(DateTime? utc) => utc.HasValue ? ToSqliteDateTime(utc.Value) : DBNull.Value;

    private static string ToSqliteDateTime(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseSqliteDateTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
