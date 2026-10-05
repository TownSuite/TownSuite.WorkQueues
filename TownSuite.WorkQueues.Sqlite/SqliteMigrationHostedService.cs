using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TownSuite.WorkQueues.Sqlite;

/// <summary>
/// Hosted service that applies idempotent DDL migrations for the SQLite workqueue schema
/// on application startup. Safe to run against an existing database.
/// </summary>
public class SqliteMigrationHostedService : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SqliteMigrationHostedService> _logger;

    public SqliteMigrationHostedService(
        IServiceProvider sp,
        ILogger<SqliteMigrationHostedService> logger)
    {
        _sp     = sp     ?? throw new ArgumentNullException(nameof(sp));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope   = _sp.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<SqliteTransportOptions>();
        await MigrateAsync(options, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task MigrateAsync(SqliteTransportOptions options, CancellationToken ct)
    {
        _logger.LogInformation("Running SQLite workqueue migrations");

        try
        {
            await using var conn = new SqliteConnection(options.ConnectionString);
            await conn.OpenAsync(ct);

            // WAL mode allows one writer and multiple concurrent readers — essential when
            // a frontend process enqueues while a worker process dequeues from the same file.
            // This setting is persisted in the database file; it only needs to be applied once.
            await Exec(conn, "PRAGMA journal_mode=WAL", ct);
            await Exec(conn, "PRAGMA busy_timeout=5000", ct);

            await Exec(conn, """
                CREATE TABLE IF NOT EXISTS workqueue (
                    id               INTEGER  PRIMARY KEY AUTOINCREMENT,
                    messageid        TEXT     NOT NULL,
                    timecreatedutc   TEXT     NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    channel          TEXT     NOT NULL,
                    payload          TEXT     NOT NULL,
                    timeprocessedutc TEXT     NULL,
                    failedat         TEXT     NULL,
                    retrycount       INTEGER  NOT NULL DEFAULT 0,
                    scheduledfor     TEXT     NULL,
                    lockeduntil      TEXT     NULL,
                    locktoken        TEXT     NULL,
                    faultdispatchedat TEXT    NULL,
                    lasterror        TEXT     NULL
                )
                """, ct);

            await Exec(conn, """
                CREATE INDEX IF NOT EXISTS IX_workqueue_channel_unprocessed
                ON workqueue (channel, timecreatedutc)
                WHERE timeprocessedutc IS NULL AND failedat IS NULL
                """, ct);

            await Exec(conn, """
                CREATE INDEX IF NOT EXISTS IX_workqueue_channel_deadlettered
                ON workqueue (channel, messageid)
                WHERE failedat IS NOT NULL
                """, ct);

            // Add lockeduntil / locktoken to existing databases that pre-date this schema.
            // IF NOT EXISTS is not universally supported for ALTER TABLE, so check PRAGMA table_info.
            await AddColumnIfMissingAsync(conn, "lockeduntil", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(conn, "locktoken",   "TEXT NULL", ct);
            await AddColumnIfMissingAsync(conn, "lasterror",   "TEXT NULL", ct);

            // Existing dead-letters are marked as already notified so the upgrade does not
            // send faults for them.
            if (await AddColumnIfMissingAsync(conn, "faultdispatchedat", "TEXT NULL", ct))
                await Exec(conn, "UPDATE workqueue SET faultdispatchedat = failedat WHERE failedat IS NOT NULL", ct);

            await Exec(conn, """
                CREATE INDEX IF NOT EXISTS IX_workqueue_channel_pendingfault
                ON workqueue (channel, failedat)
                WHERE failedat IS NOT NULL AND faultdispatchedat IS NULL
                """, ct);

            _logger.LogInformation("SQLite workqueue migrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite workqueue migration failed.");
            throw;
        }
    }

    // Returns true when the column was added.
    private static async Task<bool> AddColumnIfMissingAsync(
        SqliteConnection conn, string column, string definition, CancellationToken ct)
    {
        var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('workqueue') WHERE name = '{column}'";
        var count = Convert.ToInt32(await check.ExecuteScalarAsync(ct));
        if (count != 0) return false;

        await Exec(conn, $"ALTER TABLE workqueue ADD COLUMN {column} {definition}", ct);
        return true;
    }

    private static async Task Exec(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
