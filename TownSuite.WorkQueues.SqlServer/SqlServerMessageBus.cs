using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace TownSuite.WorkQueues.SqlServer;

/// <summary>
/// SQL Server-backed message bus with at-least-once delivery, automatic retry,
/// and dead-lettering. Uses UPDLOCK + ROWLOCK + READPAST table hints so that
/// multiple concurrent consumer instances safely claim disjoint sets of messages
/// without blocking each other.
/// </summary>
/// <remarks>
/// By default a claimed batch is held in an open transaction until every message in it is
/// handled. Set <see cref="BatchOptions.ClaimLease"/> to hold claims with a lease instead, so no
/// transaction or row lock stays open while consumers run.
/// </remarks>
public class SqlServerMessageBus : MessageBusBase
{
    private const int PurgeBatchSize = 5000;
    private readonly SqlServerTransportOptions _options;

    public SqlServerMessageBus(SqlServerTransportOptions options, ILogger logger, IServiceProvider? serviceProvider = null)
        : base(options, logger, serviceProvider, "sqlserver")
    {
        _options = options;
        StartPolling();
    }

    private string Table => $"[{_options.Schema}].[workqueue]";

    // ── Publishing ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="connection"/> must be a <see cref="SqlConnection"/> to the database the bus
    /// polls and <paramref name="transaction"/> (when supplied) a <see cref="SqlTransaction"/> on it.
    /// </remarks>
    public override async Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (connection is not SqlConnection sqlConnection)
            throw new ArgumentException("connection must be a SqlConnection", nameof(connection));
        if (transaction != null && transaction is not SqlTransaction)
            throw new ArgumentException("transaction must be a SqlTransaction", nameof(transaction));

        if (sqlConnection.State == ConnectionState.Closed)
            await sqlConnection.OpenAsync(cancellationToken);

        var channel = ChannelName<T>();
        var messageId = Guid.NewGuid();
        await InsertAsync(sqlConnection, transaction as SqlTransaction, channel, JsonSerializer.Serialize(message),
            messageId, options ?? new PublishOptions(), cancellationToken);
        RecordPublished(channel);
        return messageId;
    }

    private protected override async Task InsertAsync(string channel, string payload, Guid messageId,
        PublishOptions options, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await InsertAsync(conn, null, channel, payload, messageId, options, cancellationToken);
    }

    private async Task InsertAsync(SqlConnection conn, SqlTransaction? tran, string channel, string payload,
        Guid messageId, PublishOptions options, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand($"""
            INSERT INTO {Table} ([channel], [payload], [messageid], [scheduledfor], [expiresat])
            VALUES (@channel, @payload, @messageid, @scheduledfor, @expiresat)
            """, conn, tran);
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@messageid", messageId);
        cmd.Parameters.Add(DateTimeParam("@scheduledfor", options.DeliverAfter?.UtcDateTime));
        cmd.Parameters.Add(DateTimeParam("@expiresat", options.ExpiresAt?.UtcDateTime));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlParameter DateTimeParam(string name, DateTime? value) =>
        new(name, SqlDbType.DateTime) { Value = value.HasValue ? value.Value : DBNull.Value };

    // ── Claiming ────────────────────────────────────────────────────────────

    private protected override async Task<ClaimedBatch> ClaimAsync(string[] channels, int maxMessages, CancellationToken cancellationToken)
    {
        // SQL Server has no array parameter type. Build a safe IN clause using auto-named
        // parameters — channel names come from typeof(T).FullName so are developer-controlled.
        var inClause = string.Join(", ", channels.Select((_, i) => $"@ch{i}"));

        // UPDLOCK + ROWLOCK + READPAST: this connection claims the rows exclusively; other
        // connections with the same hints skip these rows rather than blocking. Rows held by
        // a live lease are skipped too, so lease and transaction claimers can share a table.
        var due = $"""
            SELECT TOP (@maxMessages) *
            FROM {Table} WITH (UPDLOCK, ROWLOCK, READPAST)
            WHERE [timeprocessedutc] IS NULL
              AND [failedat] IS NULL
              AND ([scheduledfor] IS NULL OR [scheduledfor] <= GETUTCDATE())
              AND ([lockeduntil] IS NULL OR [lockeduntil] < GETUTCDATE())
              AND [channel] IN ({inClause})
            ORDER BY [timecreatedutc]
            """;

        var conn = new SqlConnection(_options.ConnectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);

            if (_options.ClaimLease is { } lease)
            {
                var token = Guid.NewGuid();
                await using var cmd = new SqlCommand($"""
                    WITH due AS ({due})
                    UPDATE due
                    SET [lockeduntil] = DATEADD(millisecond, @leaseMs, GETUTCDATE()), [locktoken] = @token
                    OUTPUT inserted.[id], inserted.[channel], inserted.[payload], inserted.[retrycount],
                           inserted.[messageid], inserted.[timecreatedutc], inserted.[expiresat]
                    """, conn);
                AddClaimParameters(cmd, channels, maxMessages);
                cmd.Parameters.AddWithValue("@leaseMs", (int)Math.Min(int.MaxValue, lease.TotalMilliseconds));
                cmd.Parameters.AddWithValue("@token", token);
                var messages = await ReadMessagesAsync(cmd, cancellationToken);
                return new Batch(this, conn, null, token, messages);
            }
            else
            {
                var tran = conn.BeginTransaction();
                await using var cmd = new SqlCommand($"""
                    SELECT [id], [channel], [payload], [retrycount], [messageid], [timecreatedutc], [expiresat]
                    FROM ({due}) AS due
                    """, conn, tran);
                AddClaimParameters(cmd, channels, maxMessages);
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

    private static void AddClaimParameters(SqlCommand cmd, string[] channels, int maxMessages)
    {
        cmd.Parameters.AddWithValue("@maxMessages", maxMessages);
        for (int i = 0; i < channels.Length; i++)
            cmd.Parameters.AddWithValue($"@ch{i}", channels[i]);
    }

    private static async Task<List<MessageDto>> ReadMessagesAsync(SqlCommand cmd, CancellationToken cancellationToken)
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
        // OUTPUT does not preserve the CTE's order.
        return messages.OrderBy(m => m.TimeCreatedUtc).ThenBy(m => m.Id).ToList();
    }

    private sealed class Batch : ClaimedBatch
    {
        private readonly SqlServerMessageBus _bus;
        private readonly SqlConnection _conn;
        private readonly SqlTransaction? _tran;
        private readonly Guid? _leaseToken;

        public Batch(SqlServerMessageBus bus, SqlConnection conn, SqlTransaction? tran, Guid? leaseToken, List<MessageDto> messages)
        {
            _bus = bus;
            _conn = conn;
            _tran = tran;
            _leaseToken = leaseToken;
            Messages = messages;
        }

        // With a lease, an update only applies while this claim still holds the row.
        private string LeaseGuard => _leaseToken.HasValue ? " AND [locktoken] = @token" : string.Empty;

        public override async Task CompleteAsync(MessageDto message)
        {
            await using var cmd = new SqlCommand($"""
                UPDATE {_bus.Table}
                SET [timeprocessedutc] = GETUTCDATE(), [lockeduntil] = NULL, [locktoken] = NULL
                WHERE [id] = @id{LeaseGuard}
                """, _conn, _tran);
            await ExecuteAsync(cmd, message);
        }

        public override async Task FailAsync(MessageDto message, FailureOutcome outcome)
        {
            await using var cmd = new SqlCommand($"""
                UPDATE {_bus.Table}
                SET [retrycount]        = @attempt,
                    [failedat]          = CASE WHEN @deadLetter = 1 THEN GETUTCDATE() ELSE NULL END,
                    [faultdispatchedat] = NULL,
                    [scheduledfor]      = @scheduledFor,
                    [lasterror]         = @lastError,
                    [lockeduntil]       = NULL,
                    [locktoken]         = NULL
                WHERE [id] = @id{LeaseGuard}
                """, _conn, _tran);
            cmd.Parameters.AddWithValue("@attempt", outcome.Attempt);
            cmd.Parameters.AddWithValue("@deadLetter", outcome.DeadLetter);
            cmd.Parameters.Add(DateTimeParam("@scheduledFor", outcome.ScheduledForUtc));
            cmd.Parameters.AddWithValue("@lastError", outcome.Error.ToJson());
            await ExecuteAsync(cmd, message);
        }

        private async Task ExecuteAsync(SqlCommand cmd, MessageDto message)
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
        var inClause = string.Join(", ", channels.Select((_, i) => $"@ch{i}"));
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            WITH due AS (
                SELECT TOP (@maxFaults) *
                FROM {Table} WITH (UPDLOCK, ROWLOCK, READPAST)
                WHERE [failedat] IS NOT NULL
                  AND [faultdispatchedat] IS NULL
                  AND ([scheduledfor] IS NULL OR [scheduledfor] <= GETUTCDATE())
                  AND [channel] IN ({inClause})
                ORDER BY [failedat]
            )
            UPDATE due
            SET [scheduledfor] = DATEADD(millisecond, @leaseMs, GETUTCDATE())
            OUTPUT inserted.[channel], inserted.[payload], inserted.[messageid],
                   inserted.[retrycount], inserted.[failedat], inserted.[lasterror]
            """, conn);
        cmd.Parameters.AddWithValue("@maxFaults", maxFaults);
        cmd.Parameters.AddWithValue("@leaseMs", (int)Math.Min(int.MaxValue, lease.TotalMilliseconds));
        for (int i = 0; i < channels.Length; i++)
            cmd.Parameters.AddWithValue($"@ch{i}", channels[i]);

        var due = new List<FaultInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add(new FaultInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetInt32(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                StoredError.Parse(reader.IsDBNull(5) ? null : reader.GetString(5)),
                IsRedelivery: true));
        }
        return due;
    }

    private protected override async Task MarkFaultDeliveredAsync(FaultInfo fault)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"""
            UPDATE {Table}
            SET [faultdispatchedat] = GETUTCDATE()
            WHERE [channel] = @channel AND [messageid] = @messageid AND [failedat] IS NOT NULL
            """, conn);
        cmd.Parameters.AddWithValue("@channel", fault.Channel);
        cmd.Parameters.AddWithValue("@messageid", fault.MessageId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Dead-letters, statistics and purging ────────────────────────────────

    /// <inheritdoc />
    public override async Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            UPDATE {Table}
            SET [failedat] = NULL, [retrycount] = 0, [scheduledfor] = NULL, [faultdispatchedat] = NULL,
                [expiresat] = NULL, [lockeduntil] = NULL, [locktoken] = NULL
            WHERE [channel] = @channel AND [failedat] IS NOT NULL
            """, conn);
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            UPDATE {Table}
            SET [failedat] = NULL, [retrycount] = 0, [scheduledfor] = NULL, [faultdispatchedat] = NULL,
                [expiresat] = NULL, [lockeduntil] = NULL, [locktoken] = NULL
            WHERE [channel] = @channel AND [messageid] = @messageid AND [failedat] IS NOT NULL
            """, conn);
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        cmd.Parameters.AddWithValue("@messageid", messageId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public override async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
    {
        var channel = ChannelName<T>();
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            SELECT
                (SELECT COUNT_BIG(*) FROM {Table}
                 WHERE [channel] = @channel AND [timeprocessedutc] IS NULL AND [failedat] IS NULL),
                (SELECT COUNT_BIG(*) FROM {Table}
                 WHERE [channel] = @channel AND [failedat] IS NOT NULL),
                (SELECT COUNT_BIG(*) FROM {Table}
                 WHERE [channel] = @channel AND [failedat] IS NOT NULL AND [faultdispatchedat] IS NULL),
                (SELECT MIN(COALESCE([scheduledfor], [timecreatedutc])) FROM {Table}
                 WHERE [channel] = @channel AND [timeprocessedutc] IS NULL AND [failedat] IS NULL
                   AND ([scheduledfor] IS NULL OR [scheduledfor] <= GETUTCDATE())),
                GETUTCDATE()
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
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            SELECT [messageid], [payload], [timecreatedutc], [failedat], [retrycount], [lasterror], [faultdispatchedat]
            FROM {Table}
            WHERE [channel] = @channel AND [failedat] IS NOT NULL
            ORDER BY [failedat] DESC, [id] DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
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
            DELETE TOP ({PurgeBatchSize}) FROM {Table}
            WHERE [timeprocessedutc] IS NOT NULL AND [timeprocessedutc] < @before
            """, processedBefore, null, cancellationToken);

    /// <inheritdoc />
    public override Task<long> PurgeDeadLettered<T>(DateTimeOffset failedBefore, CancellationToken cancellationToken = default) =>
        PurgeAsync($"""
            DELETE TOP ({PurgeBatchSize}) FROM {Table}
            WHERE [channel] = @channel AND [failedat] IS NOT NULL AND [failedat] < @before
            """, failedBefore, ChannelName<T>(), cancellationToken);

    // Deletes in batches so each statement stays short and below lock escalation.
    private async Task<long> PurgeAsync(string sql, DateTimeOffset before, string? channel, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(DateTimeParam("@before", before.UtcDateTime));
            if (channel != null) cmd.Parameters.AddWithValue("@channel", channel);

            int deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < PurgeBatchSize) return total;
        }
    }
}
