using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
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
/// the row becomes available again once <see cref="SqliteTransportOptions.LockTimeout"/> elapses —
/// unlike the Postgres/SQL Server backends where a transaction rollback makes the row
/// immediately available. Set <c>LockTimeout</c> to comfortably exceed the longest expected
/// consumer processing time.
/// </para>
/// <para>
/// WAL mode is enabled by <see cref="SqliteMigrationHostedService"/> on first startup.
/// Without it, concurrent reads and writes from separate processes would serialize
/// on file-level locks, causing unnecessary <c>SQLITE_BUSY</c> errors.
/// </para>
/// </remarks>
public class SqliteMessageBus : IMessageBus
{
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<object, Task>>> _faultHandlers = new();
    private readonly ConcurrentDictionary<Type, Func<string, Exception, int, Guid, bool, Task>> _faultDispatchers = new();
    private readonly Task[] _pollingTasks;
    private const string Transport = "sqlite";
    private readonly ILogger _logger;
    private readonly SqliteTransportOptions _options;
    private readonly IServiceProvider? _serviceProvider;

    public SqliteMessageBus(SqliteTransportOptions options, ILogger logger, IServiceProvider? serviceProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider;
        // Yield to the caller so Subscribe() calls made immediately after construction
        // are registered before the first poll cycle runs.
        _pollingTasks = Enumerable.Range(0, Math.Max(1, options.MaxConcurrency))
            .Select(_ => Task.Run(async () => { await Task.Yield(); await ProcessMessagesAsync(); }))
            .ToArray();
    }

    /// <inheritdoc />
    public bool IsPolling => _pollingTasks.All(t => !t.IsCompleted);

    /// <inheritdoc />
    public void Subscribe<T>(IConsumer<T> consumer)
    {
        var handlers = _handlers.GetOrAdd(typeof(T),
            _ => new ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>>());
        handlers.TryAdd(consumer, async (obj, messageId, sentTime) =>
        {
            if (obj is T message)
                await consumer.Consume(new SimpleConsumeContext<T>(message, _cts.Token, messageId, sentTime));
        });
        EnsureFaultDispatcher<T>();
    }

    /// <summary>
    /// Registers a scoped consumer resolved fresh from an <see cref="IServiceScope"/> on every
    /// message dispatch. Requires <see cref="IServiceProvider"/> to have been passed to the
    /// constructor (automatically supplied by <see cref="SqliteServiceExtensions.AddSqliteMessageBus"/>).
    /// </summary>
    public void Subscribe<TMessage, TConsumer>() where TConsumer : class, IConsumer<TMessage>
    {
        if (_serviceProvider == null)
            throw new InvalidOperationException(
                "Scoped consumer registration requires IServiceProvider. " +
                "Pass serviceProvider to the SqliteMessageBus constructor, " +
                "or use the AddSqliteMessageBus DI extension.");

        var handlers = _handlers.GetOrAdd(typeof(TMessage),
            _ => new ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>>());
        handlers.TryAdd(typeof(TConsumer), async (obj, messageId, sentTime) =>
        {
            if (obj is TMessage message)
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var consumer = scope.ServiceProvider.GetRequiredService<TConsumer>();
                await consumer.Consume(new SimpleConsumeContext<TMessage>(message, _cts.Token, messageId, sentTime));
            }
        });
        EnsureFaultDispatcher<TMessage>();
    }

    /// <inheritdoc />
    public void SubscribeFault<T>(IConsumer<Fault<T>> consumer)
    {
        var handlers = _faultHandlers.GetOrAdd(typeof(T),
            _ => new ConcurrentDictionary<object, Func<object, Task>>());
        handlers.TryAdd(consumer, async obj =>
        {
            if (obj is Fault<T> fault)
                await consumer.Consume(new SimpleConsumeContext<Fault<T>>(fault, _cts.Token));
        });
    }

    private void EnsureFaultDispatcher<T>()
    {
        _faultDispatchers.TryAdd(typeof(T), async (payload, ex, attemptCount, messageId, nonRetryable) =>
        {
            if (!_faultHandlers.TryGetValue(typeof(T), out var handlers) || handlers.IsEmpty)
                return;

            var original = LegacyJsonDeserializer.Deserialize(payload, typeof(T));
            if (original is not T typedOriginal) return;

            var fault = new Fault<T>
            {
                OriginalMessage  = typedOriginal,
                ExceptionType    = ex.GetType().FullName ?? ex.GetType().Name,
                ExceptionMessage = ex.Message,
                StackTrace       = ex.StackTrace,
                FaultedAt        = DateTimeOffset.UtcNow,
                AttemptCount     = attemptCount,
                MessageId        = messageId,
                NonRetryable     = nonRetryable
            };

            await Task.WhenAll(handlers.Values.Select(h => h(fault)));
        });
    }

    /// <inheritdoc />
    public async Task Publish<T>(T message, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await InsertAsync(message, conn, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await InsertAsync(message, conn, null, deliverAfter, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="connection"/> must be a <see cref="SqliteConnection"/> to the file the bus
    /// polls and <paramref name="transaction"/> (when supplied) a <see cref="SqliteTransaction"/> on it.
    /// </remarks>
    public async Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        DateTimeOffset? deliverAfter = null, CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection sqliteConnection)
            throw new ArgumentException("connection must be a SqliteConnection", nameof(connection));
        if (transaction != null && transaction is not SqliteTransaction)
            throw new ArgumentException("transaction must be a SqliteTransaction", nameof(transaction));

        if (sqliteConnection.State == System.Data.ConnectionState.Closed)
            await sqliteConnection.OpenAsync(cancellationToken);

        return await InsertAsync(message, sqliteConnection, transaction as SqliteTransaction, deliverAfter, cancellationToken);
    }

    private static async Task<Guid> InsertAsync<T>(T message, SqliteConnection conn, SqliteTransaction? tran,
        DateTimeOffset? deliverAfter, CancellationToken cancellationToken)
    {
        var channel   = ChannelName<T>();
        var payload   = JsonSerializer.Serialize(message);
        var messageId = Guid.NewGuid();

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "INSERT INTO workqueue(channel, payload, messageid, scheduledfor) VALUES(@channel, @payload, @messageid, @scheduledfor)";
        cmd.Parameters.AddWithValue("@channel",     channel);
        cmd.Parameters.AddWithValue("@payload",     payload);
        cmd.Parameters.AddWithValue("@messageid",   messageId.ToString());
        cmd.Parameters.AddWithValue("@scheduledfor",
            deliverAfter.HasValue ? ToSqliteDateTime(deliverAfter.Value.UtcDateTime) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        WorkQueueMetrics.RecordPublished(Transport, channel);
        return messageId;
    }

    private static string ChannelName<T>()
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");
        if (channel.Length > 500)
            throw new ArgumentException($"Message type name exceeds 500 characters: {channel}");
        return channel;
    }

    /// <inheritdoc />
    public async Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default)
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workqueue
            SET failedat = NULL, retrycount = 0, scheduledfor = NULL,
                lockeduntil = NULL, locktoken = NULL
            WHERE channel = @channel AND failedat IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@channel", channel);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workqueue
            SET failedat = NULL, retrycount = 0, scheduledfor = NULL,
                lockeduntil = NULL, locktoken = NULL
            WHERE channel = @channel AND messageid = @messageid AND failedat IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@channel",   ChannelName<T>());
        cmd.Parameters.AddWithValue("@messageid", messageId.ToString());
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
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
            OldestReadySince  = reader.IsDBNull(2)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            CapturedAt        = new DateTimeOffset(now, TimeSpan.Zero)
        };
    }

    private async Task ProcessMessagesAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                int processed = await ClaimMessagesAsync(_options.MaxBatchSize);
                if (processed == 0)
                    await WaitAsync();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SqliteMessageBus polling loop");
                try { await Task.Delay(1000, _cts.Token); } catch (TaskCanceledException) { }
            }
        }
    }

    private async Task WaitAsync()
    {
        if (!_options.ContinuousPolling)
            await Task.Delay(_options.MaxWaitTime, _cts.Token);
    }

    private async Task<int> ClaimMessagesAsync(int maxMessages)
    {
        if (_handlers.Count == 0) return 0;

        var channelNames = _handlers.Keys
            .Select(t => t.FullName)
            .OfType<string>()
            .ToArray();

        if (channelNames.Length == 0) return 0;

        var lockToken   = Guid.NewGuid().ToString();
        var now         = ToSqliteDateTime(DateTime.UtcNow);
        var lockedUntil = ToSqliteDateTime(DateTime.UtcNow.Add(_options.LockTimeout));

        var paramNames = Enumerable.Range(0, channelNames.Length).Select(i => $"@ch{i}").ToArray();
        var inClause   = string.Join(", ", paramNames);

        await using var conn = await OpenConnectionAsync(_cts.Token);

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
            claimCmd.Parameters.AddWithValue("@lockeduntil", lockedUntil);
            claimCmd.Parameters.AddWithValue("@locktoken",   lockToken);
            claimCmd.Parameters.AddWithValue("@now",         now);
            claimCmd.Parameters.AddWithValue("@maxMessages", maxMessages);
            for (int i = 0; i < channelNames.Length; i++)
                claimCmd.Parameters.AddWithValue(paramNames[i], channelNames[i]);

            await claimCmd.ExecuteNonQueryAsync(_cts.Token);
        }

        var messages = new List<MessageDto>();
        await using (var fetchCmd = conn.CreateCommand())
        {
            fetchCmd.CommandText = """
                SELECT id, channel, payload, retrycount, messageid, timecreatedutc
                FROM workqueue
                WHERE locktoken = @locktoken
                ORDER BY timecreatedutc
                """;
            fetchCmd.Parameters.AddWithValue("@locktoken", lockToken);

            await using var reader = await fetchCmd.ExecuteReaderAsync(_cts.Token);
            while (await reader.ReadAsync(_cts.Token))
            {
                messages.Add(new MessageDto
                {
                    Id             = reader.GetInt32(0),
                    Channel        = reader.GetString(1),
                    Payload        = reader.GetString(2),
                    RetryCount     = reader.GetInt32(3),
                    MessageId      = Guid.Parse(reader.GetString(4)),
                    TimeCreatedUtc = DateTime.Parse(reader.GetString(5), null, DateTimeStyles.RoundtripKind)
                });
            }
        }

        foreach (var msg in messages)
        {
            Exception? lastException = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await DispatchMessageAsync(msg);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogError(ex, "Handler failed for message {Id} on channel {Channel}", msg.Id, msg.Channel);
            }
            stopwatch.Stop();

            if (lastException == null)
            {
                await using var completeCmd = conn.CreateCommand();
                completeCmd.CommandText = """
                    UPDATE workqueue
                    SET timeprocessedutc = @now, lockeduntil = NULL, locktoken = NULL
                    WHERE id = @id
                    """;
                completeCmd.Parameters.AddWithValue("@now", ToSqliteDateTime(DateTime.UtcNow));
                completeCmd.Parameters.AddWithValue("@id",  msg.Id);
                await completeCmd.ExecuteNonQueryAsync(_cts.Token);
                WorkQueueMetrics.RecordProcessed(Transport, msg.Channel, stopwatch.Elapsed);
            }
            else
            {
                int attempt = msg.RetryCount + 1;
                bool nonRetryable = !_options.ShouldRetry(lastException);
                bool willDeadLetter = nonRetryable || attempt >= _options.MaxRetries;
                var retryDelay = _options.GetRetryDelay(attempt);

                object scheduledFor = !willDeadLetter && retryDelay > TimeSpan.Zero
                    ? ToSqliteDateTime(DateTime.UtcNow.Add(retryDelay))
                    : DBNull.Value;
                object failedAt = willDeadLetter
                    ? ToSqliteDateTime(DateTime.UtcNow)
                    : DBNull.Value;

                await using var retryCmd = conn.CreateCommand();
                retryCmd.CommandText = """
                    UPDATE workqueue
                    SET retrycount   = retrycount + 1,
                        failedat     = @failedat,
                        scheduledfor = @scheduledfor,
                        lockeduntil  = NULL,
                        locktoken    = NULL
                    WHERE id = @id
                    """;
                retryCmd.Parameters.AddWithValue("@id",          msg.Id);
                retryCmd.Parameters.AddWithValue("@failedat",    failedAt);
                retryCmd.Parameters.AddWithValue("@scheduledfor", scheduledFor);
                await retryCmd.ExecuteNonQueryAsync(_cts.Token);

                if (willDeadLetter)
                {
                    WorkQueueMetrics.RecordDeadLettered(Transport, msg.Channel, stopwatch.Elapsed);

                    // The UPDATE above auto-commits, so the dead-letter is durable before the
                    // fault consumer runs.
                    var msgType = _handlers.Keys.FirstOrDefault(t => t.FullName == msg.Channel);
                    if (msgType != null && _faultDispatchers.TryGetValue(msgType, out var dispatcher))
                    {
                        try { await dispatcher(msg.Payload, lastException, attempt, msg.MessageId, nonRetryable); }
                        catch (Exception fex)
                        {
                            _logger.LogError(fex, "Fault consumer threw for dead-lettered message {Id}", msg.Id);
                        }
                    }
                }
                else
                {
                    WorkQueueMetrics.RecordRetried(Transport, msg.Channel, stopwatch.Elapsed);
                }
            }
        }

        return messages.Count;
    }

    private async Task DispatchMessageAsync(MessageDto msg)
    {
        var type = _handlers.Keys.FirstOrDefault(t => t.FullName == msg.Channel);
        if (type == null)
        {
            _logger.LogWarning("No handler registered for channel {Channel} — message {Id} will be skipped", msg.Channel, msg.Id);
            return;
        }

        if (!_handlers.TryGetValue(type, out var handlers)) return;

        var message  = LegacyJsonDeserializer.Deserialize(msg.Payload, type);
        var sentTime = new DateTimeOffset(msg.TimeCreatedUtc, TimeSpan.Zero);
        await Task.WhenAll(handlers.Values.Select(h => h(message!, msg.MessageId, sentTime)));
    }

    // Opens a connection and sets busy_timeout so concurrent writes retry rather than
    // failing immediately with SQLITE_BUSY.
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new SqliteConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        cmd.Dispose();
        return conn;
    }

    private static string ToSqliteDateTime(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try { await Task.WhenAll(_pollingTasks).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        _cts.Dispose();
    }
}
