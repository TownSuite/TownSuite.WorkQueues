using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace TownSuite.WorkQueues.Server.Stores;

/// <summary>
/// Topics in PostgreSQL: a <c>topicsubscription</c> row per subscription and a <c>topicdelivery</c>
/// row per copy, written for every active subscription in one <c>INSERT … SELECT</c>. Claims use
/// <c>FOR UPDATE SKIP LOCKED</c> and a lease token, so replicas of the server share the work.
/// Suited to low-volume topics such as cache invalidation and configuration change events.
/// </summary>
internal sealed class PostgresTopicStore : ITopicStore, IAsyncDisposable
{
    private const string UtcNow = "(now() AT TIME ZONE 'utc')";
    private const int PurgeBatchSize = 5000;

    private readonly TopicServerOptions _options;
    private readonly ILogger _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly int[] _retryDelaysMs;

    public PostgresTopicStore(TopicServerOptions options, ILogger<PostgresTopicStore> logger)
    {
        _options    = options;
        _logger     = logger;
        _dataSource = NpgsqlDataSource.Create(options.ConnectionString
            ?? throw new InvalidOperationException($"{TopicServerOptions.SectionName}:ConnectionString is required for the postgres backend."));

        // Retry delay by attempt number (1-based), for the nack statement.
        _retryDelaysMs = Enumerable.Range(1, Math.Max(1, options.MaxRetries))
            .Select(a => (int)Math.Min(int.MaxValue, options.GetRetryDelay(a).TotalMilliseconds))
            .ToArray();
    }

    private string Schema => _options.Schema ?? "public";
    private string Subscriptions => $"{Schema}.topicsubscription";
    private string Deliveries => $"{Schema}.topicdelivery";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TownSuite.WorkQueues.Server.sql.topics.sql")!;
        var sql = (await new StreamReader(stream).ReadToEndAsync(cancellationToken)).Replace("public.", $"{Schema}.");

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {Schema}", conn))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT 1");
        return await cmd.ExecuteScalarAsync(cancellationToken) is 1;
    }

    // ── Subscriptions ───────────────────────────────────────────────────────

    public async Task<DateTimeOffset> UpsertSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand($"""
            INSERT INTO {Subscriptions} (topic, subscriberid, expiresat)
            VALUES (@topic, @sub, {UtcNow} + @idleMs * interval '1 millisecond')
            ON CONFLICT (topic, subscriberid) DO UPDATE SET expiresat = EXCLUDED.expiresat
            RETURNING expiresat
            """);
        AddSubscription(cmd, topic, subscriberId);
        cmd.Parameters.AddWithValue("@idleMs", _options.SubscriptionIdleTimeout.TotalMilliseconds);
        return AsUtc((DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!);
    }

    public async Task<bool> TouchSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand($"""
            UPDATE {Subscriptions}
            SET expiresat = {UtcNow} + @idleMs * interval '1 millisecond'
            WHERE topic = @topic AND subscriberid = @sub AND expiresat > {UtcNow}
            """);
        AddSubscription(cmd, topic, subscriberId);
        cmd.Parameters.AddWithValue("@idleMs", _options.SubscriptionIdleTimeout.TotalMilliseconds);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) > 0) return true;

        // Expired (or never existed): remove it now rather than waiting for the cleanup.
        await RemoveSubscriptionAsync(topic, subscriberId, cancellationToken);
        return false;
    }

    public async Task<bool> RemoveSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tran = await conn.BeginTransactionAsync(cancellationToken);
        await using (var copies = new NpgsqlCommand($"""
            DELETE FROM {Deliveries}
            WHERE topic = @topic AND subscriberid = @sub AND ackedat IS NULL AND failedat IS NULL
            """, conn, tran))
        {
            AddSubscription(copies, topic, subscriberId);
            await copies.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var cmd = new NpgsqlCommand($"DELETE FROM {Subscriptions} WHERE topic = @topic AND subscriberid = @sub", conn, tran);
        AddSubscription(cmd, topic, subscriberId);
        var removed = await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        await tran.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<SubscriptionStats?> GetStatsAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand($"""
            SELECT s.expiresat,
                   (SELECT COUNT(*) FROM {Deliveries} d
                    WHERE d.topic = s.topic AND d.subscriberid = s.subscriberid AND d.ackedat IS NULL AND d.failedat IS NULL),
                   (SELECT COUNT(*) FROM {Deliveries} d
                    WHERE d.topic = s.topic AND d.subscriberid = s.subscriberid AND d.failedat IS NOT NULL)
            FROM {Subscriptions} s
            WHERE s.topic = @topic AND s.subscriberid = @sub AND s.expiresat > {UtcNow}
            """);
        AddSubscription(cmd, topic, subscriberId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SubscriptionStats(AsUtc(reader.GetDateTime(0)), reader.GetInt64(1), reader.GetInt64(2));
    }

    // ── Messages ────────────────────────────────────────────────────────────

    public async Task<int> PublishAsync(string topic, TopicMessage message, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand($"""
            INSERT INTO {Deliveries} (messageid, topic, subscriberid, payload, tracecontext, publishedat, availableat)
            SELECT @messageid, s.topic, s.subscriberid, @payload, @tracecontext, {UtcNow}, {UtcNow}
            FROM {Subscriptions} s
            WHERE s.topic = @topic AND s.expiresat > {UtcNow}
            """);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@messageid", message.MessageId);
        cmd.Parameters.AddWithValue("@payload", message.Payload);
        cmd.Parameters.Add(new NpgsqlParameter("@tracecontext", NpgsqlDbType.Text) { Value = (object?)message.TraceContext ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TopicDelivery>> ClaimAsync(string topic, string subscriberId, int maxMessages, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Copies delivered MaxRetries times without an ack are dead-lettered, not delivered again.
        await using (var deadLetter = new NpgsqlCommand($"""
            UPDATE {Deliveries}
            SET failedat = {UtcNow}, locktoken = NULL,
                lasterror = COALESCE(lasterror, 'Not acknowledged after ' || attempts || ' deliveries.')
            WHERE id IN (
                SELECT id FROM {Deliveries}
                WHERE topic = @topic AND subscriberid = @sub AND ackedat IS NULL AND failedat IS NULL
                  AND availableat <= {UtcNow} AND attempts >= @maxRetries
                FOR UPDATE SKIP LOCKED)
            RETURNING messageid, attempts
            """, conn))
        {
            AddSubscription(deadLetter, topic, subscriberId);
            deadLetter.Parameters.AddWithValue("@maxRetries", _options.MaxRetries);
            await using var reader = await deadLetter.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                TopicStoreLog.DeadLettered(_logger, topic, subscriberId, reader.GetGuid(0), reader.GetInt32(1));
        }

        var token = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand($"""
            UPDATE {Deliveries}
            SET attempts = attempts + 1,
                availableat = {UtcNow} + @leaseMs * interval '1 millisecond',
                locktoken = @token
            WHERE id IN (
                SELECT id FROM {Deliveries}
                WHERE topic = @topic AND subscriberid = @sub AND ackedat IS NULL AND failedat IS NULL
                  AND availableat <= {UtcNow} AND attempts < @maxRetries
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT @max)
            RETURNING id, messageid, publishedat, attempts, tracecontext, payload
            """, conn);
        AddSubscription(cmd, topic, subscriberId);
        cmd.Parameters.AddWithValue("@leaseMs", _options.ClaimLease.TotalMilliseconds);
        cmd.Parameters.AddWithValue("@token", token);
        cmd.Parameters.AddWithValue("@maxRetries", _options.MaxRetries);
        cmd.Parameters.AddWithValue("@max", maxMessages);

        var claimed = new List<(long Id, TopicDelivery Delivery)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                claimed.Add((id, new TopicDelivery(
                    reader.GetGuid(1),
                    $"{id}.{token:N}",
                    AsUtc(reader.GetDateTime(2)),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5))));
            }
        }
        // RETURNING does not preserve the subquery's order.
        return claimed.OrderBy(c => c.Id).Select(c => c.Delivery).ToList();
    }

    public Task<ReceiptOutcome> AckAsync(string topic, string subscriberId, string receipt, CancellationToken cancellationToken) =>
        WithReceiptAsync(topic, subscriberId, receipt, $"""
            UPDATE {Deliveries}
            SET ackedat = {UtcNow}, locktoken = NULL
            WHERE id = @id AND topic = @topic AND subscriberid = @sub AND locktoken = @token AND ackedat IS NULL AND failedat IS NULL
            """, null, cancellationToken);

    public Task<ReceiptOutcome> NackAsync(string topic, string subscriberId, string receipt, string? error, CancellationToken cancellationToken) =>
        WithReceiptAsync(topic, subscriberId, receipt, $"""
            UPDATE {Deliveries}
            SET locktoken = NULL,
                availableat = {UtcNow} + (@delaysMs)[LEAST(GREATEST(attempts, 1), cardinality(@delaysMs))] * interval '1 millisecond',
                lasterror = COALESCE(@error, lasterror)
            WHERE id = @id AND topic = @topic AND subscriberid = @sub AND locktoken = @token AND ackedat IS NULL AND failedat IS NULL
            """, cmd =>
            {
                cmd.Parameters.AddWithValue("@delaysMs", _retryDelaysMs);
                cmd.Parameters.Add(new NpgsqlParameter("@error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
            }, cancellationToken);

    private async Task<ReceiptOutcome> WithReceiptAsync(string topic, string subscriberId, string receipt, string sql,
        Action<NpgsqlCommand>? addParameters, CancellationToken cancellationToken)
    {
        var parts = receipt.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var id) || !Guid.TryParseExact(parts[1], "N", out var token))
            return ReceiptOutcome.InvalidReceipt;

        await using var cmd = _dataSource.CreateCommand(sql);
        AddSubscription(cmd, topic, subscriberId);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@token", token);
        addParameters?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0 ? ReceiptOutcome.Done : ReceiptOutcome.LeaseLost;
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        // Expired subscriptions and their waiting copies.
        await using (var expired = _dataSource.CreateCommand($"""
            WITH gone AS (
                DELETE FROM {Subscriptions} WHERE expiresat <= {UtcNow}
                RETURNING topic, subscriberid)
            DELETE FROM {Deliveries} d USING gone
            WHERE d.topic = gone.topic AND d.subscriberid = gone.subscriberid
              AND d.ackedat IS NULL AND d.failedat IS NULL
            """))
            await expired.ExecuteNonQueryAsync(cancellationToken);

        await PurgeAsync("ackedat", _options.RetainAcknowledged, cancellationToken);
        await PurgeAsync("failedat", _options.RetainDeadLettered, cancellationToken);
    }

    // Deletes in batches so each statement stays short.
    private async Task PurgeAsync(string column, TimeSpan retain, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var cmd = _dataSource.CreateCommand($"""
                DELETE FROM {Deliveries} WHERE id IN (
                    SELECT id FROM {Deliveries}
                    WHERE {column} IS NOT NULL AND {column} < {UtcNow} - @retainMs * interval '1 millisecond'
                    LIMIT {PurgeBatchSize})
                """);
            cmd.Parameters.AddWithValue("@retainMs", retain.TotalMilliseconds);
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) < PurgeBatchSize) return;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void AddSubscription(NpgsqlCommand cmd, string topic, string subscriberId)
    {
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@sub", subscriberId);
    }

    // Columns are TIMESTAMP (without time zone) holding UTC.
    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
