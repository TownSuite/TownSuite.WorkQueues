using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace TownSuite.WorkQueues.Server.Stores;

/// <summary>
/// Topics in SQL Server: a <c>topicsubscription</c> row per subscription and a <c>topicdelivery</c>
/// row per copy, written for every active subscription in one <c>INSERT … SELECT</c>. Claims use
/// <c>UPDLOCK, ROWLOCK, READPAST</c> and a lease token, so replicas of the server share the work.
/// Suited to low-volume topics such as cache invalidation and configuration change events.
/// </summary>
internal sealed partial class SqlServerTopicStore : ITopicStore
{
    private const string UtcNow = "SYSUTCDATETIME()";
    private const int PurgeBatchSize = 5000;

    private readonly TopicServerOptions _options;
    private readonly ILogger _logger;
    private readonly string _connectionString;
    private readonly string _schema;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex Identifier();

    public SqlServerTopicStore(TopicServerOptions options, ILogger<SqlServerTopicStore> logger)
    {
        _options = options;
        _logger  = logger;
        _connectionString = options.ConnectionString
            ?? throw new InvalidOperationException($"{TopicServerOptions.SectionName}:ConnectionString is required for the sqlserver backend.");
        _schema = options.Schema ?? "dbo";
        if (!Identifier().IsMatch(_schema))
            throw new InvalidOperationException($"{TopicServerOptions.SectionName}:Schema '{_schema}' is not a valid SQL Server schema name.");
    }

    private string Subscriptions => $"[{_schema}].[topicsubscription]";
    private string Deliveries => $"[{_schema}].[topicdelivery]";

    // DATEADD takes an int, so a span in milliseconds is added as whole seconds plus the remainder.
    private static string NowPlus(string msParameter) =>
        $"DATEADD(millisecond, {msParameter} % 1000, DATEADD(second, {msParameter} / 1000, {UtcNow}))";

    private static string NowMinus(string msParameter) =>
        $"DATEADD(millisecond, -({msParameter} % 1000), DATEADD(second, -({msParameter} / 1000), {UtcNow}))";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TownSuite.WorkQueues.Server.sql.topics.sqlserver.sql")!;
        var sql = (await new StreamReader(stream).ReadToEndAsync(cancellationToken))
            .Replace("[dbo]", $"[{_schema}]")
            .Replace("N'dbo'", $"N'{_schema}'");

        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand("SELECT 1", conn);
        return await cmd.ExecuteScalarAsync(cancellationToken) is 1;
    }

    // ── Subscriptions ───────────────────────────────────────────────────────

    public async Task<DateTimeOffset> UpsertSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            DECLARE @expiresat DATETIME2 = {NowPlus("@idleMs")};
            UPDATE {Subscriptions} WITH (UPDLOCK, SERIALIZABLE)
            SET [expiresat] = @expiresat
            WHERE [topic] = @topic AND [subscriberid] = @sub;
            IF @@ROWCOUNT = 0
                INSERT INTO {Subscriptions} ([topic], [subscriberid], [expiresat]) VALUES (@topic, @sub, @expiresat);
            SELECT @expiresat;
            """, conn);
        AddSubscription(cmd, topic, subscriberId);
        cmd.Parameters.AddWithValue("@idleMs", (long)_options.SubscriptionIdleTimeout.TotalMilliseconds);
        return AsUtc((DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!);
    }

    public async Task<bool> TouchSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using (var conn = await OpenAsync(cancellationToken))
        await using (var cmd = new SqlCommand($"""
            UPDATE {Subscriptions}
            SET [expiresat] = {NowPlus("@idleMs")}
            WHERE [topic] = @topic AND [subscriberid] = @sub AND [expiresat] > {UtcNow}
            """, conn))
        {
            AddSubscription(cmd, topic, subscriberId);
            cmd.Parameters.AddWithValue("@idleMs", (long)_options.SubscriptionIdleTimeout.TotalMilliseconds);
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) > 0) return true;
        }

        // Expired (or never existed): remove it now rather than waiting for the cleanup.
        await RemoveSubscriptionAsync(topic, subscriberId, cancellationToken);
        return false;
    }

    public async Task<bool> RemoveSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM {Deliveries}
            WHERE [topic] = @topic AND [subscriberid] = @sub AND [ackedat] IS NULL AND [failedat] IS NULL;
            DELETE FROM {Subscriptions} WHERE [topic] = @topic AND [subscriberid] = @sub;
            DECLARE @removed INT = @@ROWCOUNT;
            COMMIT;
            SELECT @removed;
            """, conn);
        AddSubscription(cmd, topic, subscriberId);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    public async Task<SubscriptionStats?> GetStatsAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            SELECT s.[expiresat],
                   (SELECT COUNT_BIG(*) FROM {Deliveries} d
                    WHERE d.[topic] = s.[topic] AND d.[subscriberid] = s.[subscriberid] AND d.[ackedat] IS NULL AND d.[failedat] IS NULL),
                   (SELECT COUNT_BIG(*) FROM {Deliveries} d
                    WHERE d.[topic] = s.[topic] AND d.[subscriberid] = s.[subscriberid] AND d.[failedat] IS NOT NULL)
            FROM {Subscriptions} s
            WHERE s.[topic] = @topic AND s.[subscriberid] = @sub AND s.[expiresat] > {UtcNow}
            """, conn);
        AddSubscription(cmd, topic, subscriberId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SubscriptionStats(AsUtc(reader.GetDateTime(0)), reader.GetInt64(1), reader.GetInt64(2));
    }

    // ── Messages ────────────────────────────────────────────────────────────

    public async Task<int> PublishAsync(string topic, TopicMessage message, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            INSERT INTO {Deliveries} ([messageid], [topic], [subscriberid], [payload], [tracecontext], [publishedat], [availableat])
            SELECT @messageid, s.[topic], s.[subscriberid], @payload, @tracecontext, {UtcNow}, {UtcNow}
            FROM {Subscriptions} s
            WHERE s.[topic] = @topic AND s.[expiresat] > {UtcNow}
            """, conn);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@messageid", message.MessageId);
        cmd.Parameters.Add(new SqlParameter("@payload", SqlDbType.NVarChar, -1) { Value = message.Payload });
        cmd.Parameters.Add(new SqlParameter("@tracecontext", SqlDbType.NVarChar, 1000) { Value = (object?)message.TraceContext ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TopicDelivery>> ClaimAsync(string topic, string subscriberId, int maxMessages, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);

        // Copies delivered MaxRetries times without an ack are dead-lettered, not delivered again.
        await using (var deadLetter = new SqlCommand($"""
            UPDATE d
            SET [failedat] = {UtcNow}, [locktoken] = NULL,
                [lasterror] = COALESCE([lasterror], CONCAT(N'Not acknowledged after ', [attempts], N' deliveries.'))
            OUTPUT inserted.[messageid], inserted.[attempts]
            FROM {Deliveries} d WITH (UPDLOCK, ROWLOCK, READPAST)
            WHERE d.[topic] = @topic AND d.[subscriberid] = @sub AND d.[ackedat] IS NULL AND d.[failedat] IS NULL
              AND d.[availableat] <= {UtcNow} AND d.[attempts] >= @maxRetries
            """, conn))
        {
            AddSubscription(deadLetter, topic, subscriberId);
            deadLetter.Parameters.AddWithValue("@maxRetries", _options.MaxRetries);
            await using var reader = await deadLetter.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                TopicStoreLog.DeadLettered(_logger, topic, subscriberId, reader.GetGuid(0), reader.GetInt32(1));
        }

        var token = Guid.NewGuid();
        await using var cmd = new SqlCommand($"""
            WITH due AS (
                SELECT TOP (@max) *
                FROM {Deliveries} WITH (UPDLOCK, ROWLOCK, READPAST)
                WHERE [topic] = @topic AND [subscriberid] = @sub AND [ackedat] IS NULL AND [failedat] IS NULL
                  AND [availableat] <= {UtcNow} AND [attempts] < @maxRetries
                ORDER BY [id])
            UPDATE due
            SET [attempts] = [attempts] + 1, [availableat] = {NowPlus("@leaseMs")}, [locktoken] = @token
            OUTPUT inserted.[id], inserted.[messageid], inserted.[publishedat], inserted.[attempts],
                   inserted.[tracecontext], inserted.[payload]
            """, conn);
        AddSubscription(cmd, topic, subscriberId);
        cmd.Parameters.AddWithValue("@leaseMs", (long)_options.ClaimLease.TotalMilliseconds);
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
        // OUTPUT does not preserve the CTE's order.
        return claimed.OrderBy(c => c.Id).Select(c => c.Delivery).ToList();
    }

    public async Task<ReceiptOutcome> AckAsync(string topic, string subscriberId, string receipt, CancellationToken cancellationToken)
    {
        if (ParseReceipt(receipt) is not { } parsed) return ReceiptOutcome.InvalidReceipt;
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand($"""
            UPDATE {Deliveries}
            SET [ackedat] = {UtcNow}, [locktoken] = NULL
            WHERE [id] = @id AND [topic] = @topic AND [subscriberid] = @sub AND [locktoken] = @token
              AND [ackedat] IS NULL AND [failedat] IS NULL
            """, conn);
        AddReceipt(cmd, topic, subscriberId, parsed);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0 ? ReceiptOutcome.Done : ReceiptOutcome.LeaseLost;
    }

    public async Task<ReceiptOutcome> NackAsync(string topic, string subscriberId, string receipt, string? error, CancellationToken cancellationToken)
    {
        if (ParseReceipt(receipt) is not { } parsed) return ReceiptOutcome.InvalidReceipt;
        await using var conn = await OpenAsync(cancellationToken);

        // The retry delay depends on the attempt count; the token guard keeps both statements on this claim.
        int attempts;
        await using (var read = new SqlCommand($"""
            SELECT [attempts] FROM {Deliveries}
            WHERE [id] = @id AND [topic] = @topic AND [subscriberid] = @sub AND [locktoken] = @token
              AND [ackedat] IS NULL AND [failedat] IS NULL
            """, conn))
        {
            AddReceipt(read, topic, subscriberId, parsed);
            if (await read.ExecuteScalarAsync(cancellationToken) is not int value) return ReceiptOutcome.LeaseLost;
            attempts = value;
        }

        await using var cmd = new SqlCommand($"""
            UPDATE {Deliveries}
            SET [locktoken] = NULL, [availableat] = {NowPlus("@delayMs")}, [lasterror] = COALESCE(@error, [lasterror])
            WHERE [id] = @id AND [topic] = @topic AND [subscriberid] = @sub AND [locktoken] = @token
              AND [ackedat] IS NULL AND [failedat] IS NULL
            """, conn);
        AddReceipt(cmd, topic, subscriberId, parsed);
        cmd.Parameters.AddWithValue("@delayMs", (long)_options.GetRetryDelay(attempts).TotalMilliseconds);
        cmd.Parameters.Add(new SqlParameter("@error", SqlDbType.NVarChar, -1) { Value = (object?)error ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0 ? ReceiptOutcome.Done : ReceiptOutcome.LeaseLost;
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);

        // Expired subscriptions and their waiting copies, as one unit, so a subscription renewed
        // meanwhile keeps its copies.
        await using (var expired = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @gone TABLE ([topic] NVARCHAR(200), [subscriberid] NVARCHAR(200));
            DELETE FROM {Subscriptions}
            OUTPUT deleted.[topic], deleted.[subscriberid] INTO @gone
            WHERE [expiresat] <= {UtcNow};
            DELETE d FROM {Deliveries} d
            JOIN @gone g ON d.[topic] = g.[topic] AND d.[subscriberid] = g.[subscriberid]
            WHERE d.[ackedat] IS NULL AND d.[failedat] IS NULL;
            COMMIT;
            """, conn))
            await expired.ExecuteNonQueryAsync(cancellationToken);

        await PurgeAsync(conn, "ackedat", _options.RetainAcknowledged, cancellationToken);
        await PurgeAsync(conn, "failedat", _options.RetainDeadLettered, cancellationToken);
    }

    // Deletes in batches so each statement stays short.
    private async Task PurgeAsync(SqlConnection conn, string column, TimeSpan retain, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var cmd = new SqlCommand($"""
                DELETE TOP ({PurgeBatchSize}) FROM {Deliveries}
                WHERE [{column}] IS NOT NULL AND [{column}] < {NowMinus("@retainMs")}
                """, conn);
            cmd.Parameters.AddWithValue("@retainMs", (long)retain.TotalMilliseconds);
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) < PurgeBatchSize) return;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static (long Id, Guid Token)? ParseReceipt(string receipt)
    {
        var parts = receipt.Split('.');
        return parts.Length == 2 && long.TryParse(parts[0], out var id) && Guid.TryParseExact(parts[1], "N", out var token)
            ? (id, token)
            : null;
    }

    private static void AddSubscription(SqlCommand cmd, string topic, string subscriberId)
    {
        cmd.Parameters.Add(new SqlParameter("@topic", SqlDbType.NVarChar, 200) { Value = topic });
        cmd.Parameters.Add(new SqlParameter("@sub", SqlDbType.NVarChar, 200) { Value = subscriberId });
    }

    private static void AddReceipt(SqlCommand cmd, string topic, string subscriberId, (long Id, Guid Token) receipt)
    {
        AddSubscription(cmd, topic, subscriberId);
        cmd.Parameters.AddWithValue("@id", receipt.Id);
        cmd.Parameters.AddWithValue("@token", receipt.Token);
    }

    // Columns are DATETIME2 holding UTC.
    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
