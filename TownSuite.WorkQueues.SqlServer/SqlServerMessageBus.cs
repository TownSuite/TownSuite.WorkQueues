using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace TownSuite.WorkQueues.SqlServer;

/// <summary>
/// SQL Server-backed message bus with at-least-once delivery, automatic retry,
/// and dead-lettering. Uses UPDLOCK + ROWLOCK + READPAST table hints so that
/// multiple concurrent consumer instances safely claim disjoint sets of messages
/// without blocking each other.
/// </summary>
public class SqlServerMessageBus : IMessageBus
{
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<object, Task>>> _faultHandlers = new();
    private readonly ConcurrentDictionary<Type, Func<string, Exception, int, Guid, bool, Task>> _faultDispatchers = new();
    private readonly Task[] _pollingTasks;
    private const string Transport = "sqlserver";
    private readonly ILogger _logger;
    private readonly SqlServerTransportOptions _options;
    private readonly IServiceProvider? _serviceProvider;

    public SqlServerMessageBus(SqlServerTransportOptions options, ILogger logger, IServiceProvider? serviceProvider = null)
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
    /// constructor (automatically supplied by
    /// <see cref="SqlServerServiceExtensions.AddSqlServerMessageBus"/>).
    /// </summary>
    public void Subscribe<TMessage, TConsumer>() where TConsumer : class, IConsumer<TMessage>
    {
        if (_serviceProvider == null)
            throw new InvalidOperationException(
                "Scoped consumer registration requires IServiceProvider. " +
                "Pass serviceProvider to the SqlServerMessageBus constructor, " +
                "or use the AddSqlServerMessageBus DI extension.");

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
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await InsertAsync(message, conn, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await InsertAsync(message, conn, null, deliverAfter, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="connection"/> must be a <see cref="SqlConnection"/> to the database the bus
    /// polls and <paramref name="transaction"/> (when supplied) a <see cref="SqlTransaction"/> on it.
    /// </remarks>
    public async Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        DateTimeOffset? deliverAfter = null, CancellationToken cancellationToken = default)
    {
        if (connection is not SqlConnection sqlConnection)
            throw new ArgumentException("connection must be a SqlConnection", nameof(connection));
        if (transaction != null && transaction is not SqlTransaction)
            throw new ArgumentException("transaction must be a SqlTransaction", nameof(transaction));

        if (sqlConnection.State == System.Data.ConnectionState.Closed)
            await sqlConnection.OpenAsync(cancellationToken);

        return await InsertAsync(message, sqlConnection, transaction as SqlTransaction, deliverAfter, cancellationToken);
    }

    private async Task<Guid> InsertAsync<T>(T message, SqlConnection conn, SqlTransaction? tran,
        DateTimeOffset? deliverAfter, CancellationToken cancellationToken)
    {
        var channel = ChannelName<T>();
        var payload = JsonSerializer.Serialize(message);
        var messageId = Guid.NewGuid();

        var sql = $"""
            INSERT INTO [{_options.Schema}].[workqueue] ([channel], [payload], [messageid], [scheduledfor])
            VALUES (@channel, @payload, @messageid, @scheduledfor)
            """;
        await using var cmd = new SqlCommand(sql, conn, tran);
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@messageid", messageId);
        cmd.Parameters.Add(new SqlParameter("@scheduledfor", System.Data.SqlDbType.DateTime)
        {
            Value = deliverAfter.HasValue ? deliverAfter.Value.UtcDateTime : DBNull.Value
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        WorkQueueMetrics.RecordPublished(Transport, channel);
        return messageId;
    }

    private static string ChannelName<T>()
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for {typeof(T)}");

        if (channel.Length > 500)
            throw new ArgumentException($"Message type name exceeds 500 characters: {channel}");

        return channel;
    }

    /// <inheritdoc />
    public async Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default)
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for {typeof(T)}");

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"""
            UPDATE [{_options.Schema}].[workqueue]
            SET [failedat] = NULL, [retrycount] = 0, [scheduledfor] = NULL
            WHERE [channel] = @channel AND [failedat] IS NOT NULL
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@channel", channel);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"""
            UPDATE [{_options.Schema}].[workqueue]
            SET [failedat] = NULL, [retrycount] = 0, [scheduledfor] = NULL
            WHERE [channel] = @channel AND [messageid] = @messageid AND [failedat] IS NOT NULL
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        cmd.Parameters.AddWithValue("@messageid", messageId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
    {
        var channel = ChannelName<T>();
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                (SELECT COUNT_BIG(*) FROM [{_options.Schema}].[workqueue]
                 WHERE [channel] = @channel AND [timeprocessedutc] IS NULL AND [failedat] IS NULL),
                (SELECT COUNT_BIG(*) FROM [{_options.Schema}].[workqueue]
                 WHERE [channel] = @channel AND [failedat] IS NOT NULL),
                (SELECT MIN(COALESCE([scheduledfor], [timecreatedutc])) FROM [{_options.Schema}].[workqueue]
                 WHERE [channel] = @channel AND [timeprocessedutc] IS NULL AND [failedat] IS NULL
                   AND ([scheduledfor] IS NULL OR [scheduledfor] <= GETUTCDATE())),
                GETUTCDATE()
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@channel", channel);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new QueueStatistics
        {
            Channel           = channel,
            PendingCount      = reader.GetInt64(0),
            DeadLetteredCount = reader.GetInt64(1),
            OldestReadySince  = reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
            CapturedAt        = new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)
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
                _logger.LogError(ex, "Error in SqlServerMessageBus polling loop");
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

        // SQL Server has no array parameter type. Build a safe IN clause using
        // auto-named parameters — channel names come from typeof(T).FullName so
        // are developer-controlled, not user input.
        var paramNames = Enumerable.Range(0, channelNames.Length)
            .Select(i => $"@ch{i}")
            .ToArray();
        var inClause = string.Join(", ", paramNames);

        // UPDLOCK + ROWLOCK + READPAST: this connection claims the rows exclusively;
        // other connections with the same hints skip these rows rather than blocking.
        var selectSql = $"""
            SELECT TOP (@maxMessages) [id], [channel], [payload], [retrycount], [messageid], [timecreatedutc]
            FROM [{_options.Schema}].[workqueue] WITH (UPDLOCK, ROWLOCK, READPAST)
            WHERE [timeprocessedutc] IS NULL
              AND [failedat] IS NULL
              AND ([scheduledfor] IS NULL OR [scheduledfor] <= GETUTCDATE())
              AND [channel] IN ({inClause})
            ORDER BY [timecreatedutc]
            """;

        var messages = new List<MessageDto>();

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();
        await using var tran = conn.BeginTransaction();

        await using (var cmd = new SqlCommand(selectSql, conn, tran))
        {
            cmd.Parameters.AddWithValue("@maxMessages", maxMessages);
            for (int i = 0; i < channelNames.Length; i++)
                cmd.Parameters.AddWithValue(paramNames[i], channelNames[i]);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new MessageDto
                {
                    Id             = reader.GetInt32(0),
                    Channel        = reader.GetString(1),
                    Payload        = reader.GetString(2),
                    RetryCount     = reader.GetInt32(3),
                    MessageId      = reader.GetGuid(4),
                    TimeCreatedUtc = reader.GetDateTime(5)
                });
            }
        }

        // Fault consumers run only after the claim transaction commits, so a failed commit
        // never reports a dead-letter that did not happen.
        var faults = new List<(MessageDto Message, Exception Exception, bool NonRetryable)>();

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
                _logger.LogError(ex, "Handler failed for message {Id} on channel {Channel}",
                    msg.Id, msg.Channel);
            }
            stopwatch.Stop();

            if (lastException == null)
            {
                await using var upd = new SqlCommand(
                    $"UPDATE [{_options.Schema}].[workqueue] SET [timeprocessedutc] = GETUTCDATE() WHERE [id] = @id",
                    conn, tran);
                upd.Parameters.AddWithValue("@id", msg.Id);
                await upd.ExecuteNonQueryAsync();
                WorkQueueMetrics.RecordProcessed(Transport, msg.Channel, stopwatch.Elapsed);
            }
            else
            {
                int attempt = msg.RetryCount + 1;
                bool nonRetryable = !_options.ShouldRetry(lastException);
                bool willDeadLetter = nonRetryable || attempt >= _options.MaxRetries;
                var retryDelay = _options.GetRetryDelay(attempt);

                DateTime? scheduledFor = !willDeadLetter && retryDelay > TimeSpan.Zero
                    ? DateTime.UtcNow.Add(retryDelay)
                    : (DateTime?)null;

                await using var retry = new SqlCommand($"""
                    UPDATE [{_options.Schema}].[workqueue]
                    SET [retrycount]   = [retrycount] + 1,
                        [failedat]     = CASE WHEN @deadLetter = 1 THEN GETUTCDATE() ELSE NULL END,
                        [scheduledfor] = @scheduledFor
                    WHERE [id] = @id
                    """, conn, tran);
                retry.Parameters.AddWithValue("@id", msg.Id);
                retry.Parameters.AddWithValue("@deadLetter", willDeadLetter);
                retry.Parameters.Add(new SqlParameter("@scheduledFor", System.Data.SqlDbType.DateTime)
                {
                    Value = scheduledFor.HasValue ? (object)scheduledFor.Value : DBNull.Value
                });
                await retry.ExecuteNonQueryAsync();

                if (willDeadLetter)
                {
                    WorkQueueMetrics.RecordDeadLettered(Transport, msg.Channel, stopwatch.Elapsed);
                    faults.Add((msg, lastException, nonRetryable));
                }
                else
                {
                    WorkQueueMetrics.RecordRetried(Transport, msg.Channel, stopwatch.Elapsed);
                }
            }
        }

        await tran.CommitAsync();

        foreach (var (msg, exception, nonRetryable) in faults)
            await DispatchFaultAsync(msg, exception, nonRetryable);

        return messages.Count;
    }

    private async Task DispatchFaultAsync(MessageDto msg, Exception exception, bool nonRetryable)
    {
        var msgType = _handlers.Keys.FirstOrDefault(t => t.FullName == msg.Channel);
        if (msgType == null || !_faultDispatchers.TryGetValue(msgType, out var dispatcher)) return;

        try { await dispatcher(msg.Payload, exception, msg.RetryCount + 1, msg.MessageId, nonRetryable); }
        catch (Exception fex)
        {
            _logger.LogError(fex, "Fault consumer threw for dead-lettered message {Id}", msg.Id);
        }
    }

    private async Task DispatchMessageAsync(MessageDto msg)
    {
        var type = _handlers.Keys.FirstOrDefault(t => t.FullName == msg.Channel);
        if (type == null)
        {
            _logger.LogWarning(
                "No handler registered for channel {Channel} — message {Id} will be skipped",
                msg.Channel, msg.Id);
            return;
        }

        if (!_handlers.TryGetValue(type, out var handlers)) return;

        var message = LegacyJsonDeserializer.Deserialize(msg.Payload, type);
        var sentTime = new DateTimeOffset(msg.TimeCreatedUtc, TimeSpan.Zero);
        await Task.WhenAll(handlers.Values.Select(h => h(message!, msg.MessageId, sentTime)));
    }

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
