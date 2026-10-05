using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace TownSuite.WorkQueues.Postgres;

/// <summary>
/// PostgreSQL-backed message bus with at-least-once delivery, automatic retry, and
/// dead-lettering. Uses <c>FOR UPDATE SKIP LOCKED</c> so concurrent consumers claim
/// disjoint sets of messages without blocking each other.
/// </summary>
/// <remarks>
/// <para>By default a claimed batch is held in an open transaction until every message in it is
/// handled. Set <see cref="BatchOptions.ClaimLease"/> to hold claims with a lease instead.</para>
/// <para>All timestamps are UTC, taken from <c>now() AT TIME ZONE 'utc'</c>, so the database
/// session's time zone does not matter.</para>
/// </remarks>
public class PostgresMessageBus : MessageBusBase
{
    private const int PurgeBatchSize = 5000;
    private const string UtcNow = "(now() AT TIME ZONE 'utc')";
    private readonly SqlTransportOptions _options;

    public PostgresMessageBus(SqlTransportOptions options, ILogger logger, IServiceProvider? serviceProvider = null)
        : base(options, logger, serviceProvider, "postgres")
    {
        _options = options;
        StartPolling();
    }

    private string Table => $"{_options.Schema}.workqueue";

    // ── Publishing ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="connection"/> must be an <see cref="NpgsqlConnection"/> to the database the bus
    /// polls and <paramref name="transaction"/> (when supplied) an <see cref="NpgsqlTransaction"/> on it.
    /// </remarks>
    public override async Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
            throw new ArgumentException("connection must be an NpgsqlConnection", nameof(connection));
        if (transaction != null && transaction is not NpgsqlTransaction)
            throw new ArgumentException("transaction must be an NpgsqlTransaction", nameof(transaction));

        if (npgsqlConnection.State == ConnectionState.Closed)
            await npgsqlConnection.OpenAsync(cancellationToken);

        var channel = ChannelName<T>();
        var messageId = Guid.NewGuid();
        await InsertAsync(npgsqlConnection, transaction as NpgsqlTransaction, channel, JsonSerializer.Serialize(message),
            messageId, options ?? new PublishOptions(), cancellationToken);
        RecordPublished(channel);
        return messageId;
    }

    private protected override async Task InsertAsync(string channel, string payload, Guid messageId,
        PublishOptions options, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await InsertAsync(conn, null, channel, payload, messageId, options, cancellationToken);
    }

    private async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction? tran, string channel, string payload,
        Guid messageId, PublishOptions options, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {Table} (channel, payload, messageid, timecreatedutc, scheduledfor, expiresat)
            VALUES (@channel, @payload, @messageid, {UtcNow}, @scheduledfor, @expiresat)
            """, conn, tran);
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@messageid", messageId);
        cmd.Parameters.Add(TimestampParam("@scheduledfor", options.DeliverAfter?.UtcDateTime));
        cmd.Parameters.Add(TimestampParam("@expiresat", options.ExpiresAt?.UtcDateTime));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Columns are TIMESTAMP (without time zone) holding UTC.
    private static NpgsqlParameter TimestampParam(string name, DateTime? utc) =>
        new(name, NpgsqlDbType.Timestamp)
        {
            Value = utc.HasValue ? DateTime.SpecifyKind(utc.Value, DateTimeKind.Unspecified) : DBNull.Value
        };

    // ── Claiming ────────────────────────────────────────────────────────────

    private protected override async Task<ClaimedBatch> ClaimAsync(string[] channels, int maxMessages, CancellationToken cancellationToken)
    {
        // Rows held by a live lease are skipped, so lease and transaction claimers can share a table.
        var due = $"""
            SELECT id FROM {Table}
            WHERE timeprocessedutc IS NULL
              AND failedat IS NULL
              AND (scheduledfor IS NULL OR scheduledfor <= {UtcNow})
              AND (lockeduntil IS NULL OR lockeduntil < {UtcNow})
              AND channel = ANY(@channels)
            ORDER BY timecreatedutc
            FOR UPDATE SKIP LOCKED
            LIMIT @maxMessages
            """;
        const string columns = "id, channel, payload, retrycount, messageid, timecreatedutc, expiresat";

        var conn = new NpgsqlConnection(_options.ConnectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);

            if (_options.ClaimLease is { } lease)
            {
                var token = Guid.NewGuid();
                await using var cmd = new NpgsqlCommand($"""
                    UPDATE {Table}
                    SET lockeduntil = {UtcNow} + @leaseMs * interval '1 millisecond', locktoken = @token
                    WHERE id IN ({due})
                    RETURNING {columns}
                    """, conn);
                cmd.Parameters.AddWithValue("@channels", channels);
                cmd.Parameters.AddWithValue("@maxMessages", maxMessages);
                cmd.Parameters.AddWithValue("@leaseMs", lease.TotalMilliseconds);
                cmd.Parameters.AddWithValue("@token", token);
                var messages = await ReadMessagesAsync(cmd, cancellationToken);
                return new Batch(this, conn, null, token, messages);
            }
            else
            {
                var tran = await conn.BeginTransactionAsync(cancellationToken);
                await using var cmd = new NpgsqlCommand($"""
                    SELECT {columns} FROM {Table}
                    WHERE id IN ({due})
                    FOR UPDATE
                    """, conn, tran);
                cmd.Parameters.AddWithValue("@channels", channels);
                cmd.Parameters.AddWithValue("@maxMessages", maxMessages);
                var messages = await ReadMessagesAsync(cmd, cancellationToken);
                return new Batch(this, conn, tran, null, messages);
            }
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static async Task<List<MessageDto>> ReadMessagesAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        var messages = new List<MessageDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new MessageDto
            {
                Id             = reader.GetInt32(0),
                Channel        = reader.GetString(1),
                Payload        = reader.GetString(2),
                RetryCount     = reader.GetInt32(3),
                MessageId      = reader.GetGuid(4),
                TimeCreatedUtc = reader.GetDateTime(5),
                ExpiresAtUtc   = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
            });
        }
        return messages.OrderBy(m => m.TimeCreatedUtc).ThenBy(m => m.Id).ToList();
    }

    private sealed class Batch : ClaimedBatch
    {
        private readonly PostgresMessageBus _bus;
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction? _tran;
        private readonly Guid? _leaseToken;

        public Batch(PostgresMessageBus bus, NpgsqlConnection conn, NpgsqlTransaction? tran, Guid? leaseToken, List<MessageDto> messages)
        {
            _bus = bus;
            _conn = conn;
            _tran = tran;
            _leaseToken = leaseToken;
            Messages = messages;
        }

        // With a lease, an update only applies while this claim still holds the row.
        private string LeaseGuard => _leaseToken.HasValue ? " AND locktoken = @token" : string.Empty;

        public override async Task CompleteAsync(MessageDto message)
        {
            await using var cmd = new NpgsqlCommand($"""
                UPDATE {_bus.Table}
                SET timeprocessedutc = {UtcNow}, lockeduntil = NULL, locktoken = NULL
                WHERE id = @id{LeaseGuard}
                """, _conn, _tran);
            await ExecuteAsync(cmd, message);
        }

        public override async Task FailAsync(MessageDto message, FailureOutcome outcome)
        {
            await using var cmd = new NpgsqlCommand($"""
                UPDATE {_bus.Table}
                SET retrycount        = @attempt,
                    failedat          = CASE WHEN @deadLetter THEN {UtcNow} ELSE NULL END,
                    faultdispatchedat = NULL,
                    scheduledfor      = @scheduledFor,
                    lasterror         = @lastError,
                    lockeduntil       = NULL,
                    locktoken         = NULL
                WHERE id = @id{LeaseGuard}
                """, _conn, _tran);
            cmd.Parameters.AddWithValue("@attempt", outcome.Attempt);
            cmd.Parameters.AddWithValue("@deadLetter", outcome.DeadLetter);
            cmd.Parameters.Add(TimestampParam("@scheduledFor", outcome.ScheduledForUtc));
            cmd.Parameters.AddWithValue("@lastError", outcome.Error.ToJson());
            await ExecuteAsync(cmd, message);
        }

        private async Task ExecuteAsync(NpgsqlCommand cmd, MessageDto message)
        {
            cmd.Parameters.AddWithValue("@id", message.Id);
            if (_leaseToken.HasValue) cmd.Parameters.AddWithValue("@token", _leaseToken.Value);

            if (await cmd.ExecuteNonQueryAsync() == 0 && _leaseToken.HasValue)
                _bus.Logger.LogWarning(
                    "Lease on message {MessageId} expired before its outcome was recorded; it may be delivered again. Increase ClaimLease.",
                    message.MessageId);
        }

        public override async Task CommitAsync()
        {
            if (_tran != null) await _tran.CommitAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            if (_tran != null) await _tran.DisposeAsync();
            await _conn.DisposeAsync();
        }
    }

    // ── Faults ──────────────────────────────────────────────────────────────

    // The claim pushes scheduledfor forward by the lease and commits at once, so no lock is
    // held while fault consumers run.
    private protected override async Task<IReadOnlyList<FaultInfo>> ClaimDueFaultsAsync(string[] channels, int maxFaults,
        TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE {Table}
            SET scheduledfor = {UtcNow} + @leaseMs * interval '1 millisecond'
            WHERE id IN (
                SELECT id FROM {Table}
                WHERE failedat IS NOT NULL
                  AND faultdispatchedat IS NULL
                  AND (scheduledfor IS NULL OR scheduledfor <= {UtcNow})
                  AND channel = ANY(@channels)
                ORDER BY failedat
                FOR UPDATE SKIP LOCKED
                LIMIT @maxFaults
            )
            RETURNING channel, payload, messageid, retrycount, failedat, lasterror
            """, conn);
        cmd.Parameters.AddWithValue("@channels", channels);
        cmd.Parameters.AddWithValue("@maxFaults", maxFaults);
        cmd.Parameters.AddWithValue("@leaseMs", lease.TotalMilliseconds);

        var due = new List<FaultInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add(new FaultInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetInt32(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                StoredError.Parse(reader.IsDBNull(5) ? null : reader.GetString(5)),
                IsRedelivery: true));
        }
        return due;
    }

    private protected override async Task MarkFaultDeliveredAsync(FaultInfo fault)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            UPDATE {Table}
            SET faultdispatchedat = {UtcNow}
            WHERE channel = @channel AND messageid = @messageid AND failedat IS NOT NULL
            """, conn);
        cmd.Parameters.AddWithValue("@channel", fault.Channel);
        cmd.Parameters.AddWithValue("@messageid", fault.MessageId);
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
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE {Table} SET {ReplaySet}
            WHERE channel = @channel AND failedat IS NOT NULL
            """, conn);
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE {Table} SET {ReplaySet}
            WHERE channel = @channel AND messageid = @messageid AND failedat IS NOT NULL
            """, conn);
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        cmd.Parameters.AddWithValue("@messageid", messageId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public override async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
    {
        var channel = ChannelName<T>();
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"""
            SELECT
                (SELECT COUNT(*) FROM {Table}
                 WHERE channel = @channel AND timeprocessedutc IS NULL AND failedat IS NULL),
                (SELECT COUNT(*) FROM {Table}
                 WHERE channel = @channel AND failedat IS NOT NULL),
                (SELECT COUNT(*) FROM {Table}
                 WHERE channel = @channel AND failedat IS NOT NULL AND faultdispatchedat IS NULL),
                (SELECT MIN(COALESCE(scheduledfor, timecreatedutc)) FROM {Table}
                 WHERE channel = @channel AND timeprocessedutc IS NULL AND failedat IS NULL
                   AND (scheduledfor IS NULL OR scheduledfor <= {UtcNow})),
                {UtcNow}
            """, conn);
        cmd.Parameters.AddWithValue("@channel", channel);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new QueueStatistics
        {
            Channel           = channel,
            PendingCount      = reader.GetInt64(0),
            DeadLetteredCount = reader.GetInt64(1),
            PendingFaultCount = reader.GetInt64(2),
            OldestReadySince  = reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
            CapturedAt        = new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero)
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<DeadLetteredMessage<T>>> GetDeadLettered<T>(int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"""
            SELECT messageid, payload, timecreatedutc, failedat, retrycount, lasterror, faultdispatchedat
            FROM {Table}
            WHERE channel = @channel AND failedat IS NOT NULL
            ORDER BY failedat DESC, id DESC
            OFFSET @skip LIMIT @take
            """, conn);
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        cmd.Parameters.AddWithValue("@skip", Math.Max(0, skip));
        cmd.Parameters.AddWithValue("@take", Math.Max(0, take));

        var result = new List<DeadLetteredMessage<T>>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(FaultRegistry.ToDeadLettered<T>(
                reader.GetGuid(0),
                reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                !reader.IsDBNull(6)));
        }
        return result;
    }

    /// <inheritdoc />
    public override Task<long> PurgeProcessed(DateTimeOffset processedBefore, CancellationToken cancellationToken = default) =>
        PurgeAsync($"""
            DELETE FROM {Table} WHERE id IN (
                SELECT id FROM {Table}
                WHERE timeprocessedutc IS NOT NULL AND timeprocessedutc < @before
                LIMIT {PurgeBatchSize})
            """, processedBefore, null, cancellationToken);

    /// <inheritdoc />
    public override Task<long> PurgeDeadLettered<T>(DateTimeOffset failedBefore, CancellationToken cancellationToken = default) =>
        PurgeAsync($"""
            DELETE FROM {Table} WHERE id IN (
                SELECT id FROM {Table}
                WHERE channel = @channel AND failedat IS NOT NULL AND failedat < @before
                LIMIT {PurgeBatchSize})
            """, failedBefore, ChannelName<T>(), cancellationToken);

    // Deletes in batches so each statement stays short.
    private async Task<long> PurgeAsync(string sql, DateTimeOffset before, string? channel, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.Add(TimestampParam("@before", before.UtcDateTime));
            if (channel != null) cmd.Parameters.AddWithValue("@channel", channel);

            int deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < PurgeBatchSize) return total;
        }
    }
}
