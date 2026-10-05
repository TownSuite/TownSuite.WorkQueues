using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace TownSuite.WorkQueues.Postgres;

public class PostgresMessageBus : IMessageBus
{
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>>> _handlers = new();
    private readonly FaultRegistry _faults = new();
    private readonly IntervalGate _faultGate;
    private readonly Task[] _pollingTasks;
    private const string Transport = "postgres";
    private readonly ILogger _logger;
    private readonly SqlTransportOptions _options;
    private readonly IServiceProvider? _serviceProvider;

    public PostgresMessageBus(SqlTransportOptions options, ILogger logger, IServiceProvider? serviceProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider;
        _faultGate = IntervalGate.ForFaultRedelivery(options);
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
    }

    /// <summary>
    /// Registers a scoped consumer resolved fresh from an <see cref="IServiceScope"/> on every
    /// message dispatch. Requires <see cref="IServiceProvider"/> to have been passed to the
    /// constructor (automatically supplied by <see cref="PostgresMigrationHostedServiceExtensions.AddPostgresMessageBus"/>).
    /// </summary>
    public void Subscribe<TMessage, TConsumer>() where TConsumer : class, IConsumer<TMessage>
    {
        if (_serviceProvider == null)
            throw new InvalidOperationException(
                "Scoped consumer registration requires IServiceProvider. " +
                "Pass serviceProvider to the PostgresMessageBus constructor, " +
                "or use the AddPostgresMessageBus DI extension.");

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
    }

    /// <inheritdoc />
    public void SubscribeFault<T>(IConsumer<Fault<T>> consumer) => _faults.Add(consumer);

    /// <inheritdoc />
    public async Task Publish<T>(T message, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await InsertAsync(message, conn, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await InsertAsync(message, conn, null, deliverAfter, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="connection"/> must be an <see cref="NpgsqlConnection"/> to the database the bus
    /// polls and <paramref name="transaction"/> (when supplied) an <see cref="NpgsqlTransaction"/> on it.
    /// </remarks>
    public async Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        DateTimeOffset? deliverAfter = null, CancellationToken cancellationToken = default)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
            throw new ArgumentException("connection must be an NpgsqlConnection", nameof(connection));
        if (transaction != null && transaction is not NpgsqlTransaction)
            throw new ArgumentException("transaction must be an NpgsqlTransaction", nameof(transaction));

        if (npgsqlConnection.State == System.Data.ConnectionState.Closed)
            await npgsqlConnection.OpenAsync(cancellationToken);

        return await InsertAsync(message, npgsqlConnection, transaction as NpgsqlTransaction, deliverAfter, cancellationToken);
    }

    private async Task<Guid> InsertAsync<T>(T message, NpgsqlConnection conn, NpgsqlTransaction? tran,
        DateTimeOffset? deliverAfter, CancellationToken cancellationToken)
    {
        var channel = ChannelName<T>();
        var payload = JsonSerializer.Serialize(message);
        var messageId = Guid.NewGuid();

        var sql = $"INSERT INTO {_options.Schema}.workqueue(channel, payload, messageid, scheduledfor) VALUES(@channel, @payload, @messageid, @scheduledfor)";
        await using var cmd = new NpgsqlCommand(sql, conn, tran);
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@messageid", messageId);
        cmd.Parameters.Add(new NpgsqlParameter("@scheduledfor", NpgsqlTypes.NpgsqlDbType.Timestamp)
        {
            Value = deliverAfter.HasValue
                ? DateTime.SpecifyKind(deliverAfter.Value.UtcDateTime, DateTimeKind.Unspecified)
                : DBNull.Value
        });
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

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        var sql = $"""
            UPDATE {_options.Schema}.workqueue
            SET failedat = NULL, retrycount = 0, scheduledfor = NULL, faultdispatchedat = NULL
            WHERE channel = @channel AND failedat IS NOT NULL
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@channel", channel);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        var sql = $"""
            UPDATE {_options.Schema}.workqueue
            SET failedat = NULL, retrycount = 0, scheduledfor = NULL, faultdispatchedat = NULL
            WHERE channel = @channel AND messageid = @messageid AND failedat IS NOT NULL
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@channel", ChannelName<T>());
        cmd.Parameters.AddWithValue("@messageid", messageId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
    {
        var channel = ChannelName<T>();
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        // Timestamps are stored as TIMESTAMP (no zone) using CURRENT_TIMESTAMP, so compare and
        // report in the same session-local clock the rows were written with.
        var sql = $"""
            SELECT
                (SELECT COUNT(*) FROM {_options.Schema}.workqueue
                 WHERE channel = @channel AND timeprocessedutc IS NULL AND failedat IS NULL),
                (SELECT COUNT(*) FROM {_options.Schema}.workqueue
                 WHERE channel = @channel AND failedat IS NOT NULL),
                (SELECT COUNT(*) FROM {_options.Schema}.workqueue
                 WHERE channel = @channel AND failedat IS NOT NULL AND faultdispatchedat IS NULL),
                (SELECT MIN(COALESCE(scheduledfor, timecreatedutc)) FROM {_options.Schema}.workqueue
                 WHERE channel = @channel AND timeprocessedutc IS NULL AND failedat IS NULL
                   AND (scheduledfor IS NULL OR scheduledfor <= CURRENT_TIMESTAMP)),
                CURRENT_TIMESTAMP::timestamp
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
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
    public async Task<IReadOnlyList<DeadLetteredMessage<T>>> GetDeadLettered<T>(int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT messageid, payload, timecreatedutc, failedat, retrycount, lasterror, faultdispatchedat
            FROM {_options.Schema}.workqueue
            WHERE channel = @channel AND failedat IS NOT NULL
            ORDER BY failedat DESC, id DESC
            OFFSET @skip LIMIT @take
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
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

    private async Task ProcessMessagesAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                int processedCount = await ClaimMessagesAsync(_options.MaxBatchSize)
                    + (_faultGate.TryEnter() ? await RedeliverFaultsAsync(_options.MaxBatchSize) : 0);
                if (processedCount == 0)
                    await WaitAsync();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PostgresMessageBus polling loop");
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

        var messages = new List<MessageDto>();

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync();
        await using var tran = await conn.BeginTransactionAsync();

        var selectSql = $"""
            SELECT id, channel, payload, retrycount, messageid, timecreatedutc
            FROM {_options.Schema}.workqueue
            WHERE timeprocessedutc IS NULL
              AND failedat IS NULL
              AND (scheduledfor IS NULL OR scheduledfor <= CURRENT_TIMESTAMP)
              AND channel = ANY(@channels)
            ORDER BY timecreatedutc
            FOR UPDATE SKIP LOCKED
            LIMIT @maxMessages
            """;

        await using (var cmd = new NpgsqlCommand(selectSql, conn, tran))
        {
            cmd.Parameters.AddWithValue("@channels", channelNames);
            cmd.Parameters.AddWithValue("@maxMessages", maxMessages);
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
        var faults = new List<FaultInfo>();

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
                await using var updateCmd = new NpgsqlCommand(
                    $"UPDATE {_options.Schema}.workqueue SET timeprocessedutc = CURRENT_TIMESTAMP WHERE id = @id",
                    conn, tran);
                updateCmd.Parameters.AddWithValue("id", msg.Id);
                await updateCmd.ExecuteNonQueryAsync();
                WorkQueueMetrics.RecordProcessed(Transport, msg.Channel, stopwatch.Elapsed);
            }
            else
            {
                int attempt = msg.RetryCount + 1;
                bool nonRetryable = !_options.ShouldRetry(lastException);
                bool willDeadLetter = nonRetryable || attempt >= _options.MaxRetries;
                var retryDelay = _options.GetRetryDelay(attempt);

                // A retried message is held back by its retry delay. A dead-lettered row reuses
                // scheduledfor as the time its fault may be redelivered, in case the fault consumer
                // below does not run or does not finish.
                var holdFor = willDeadLetter ? _options.FaultRedeliveryDelay : retryDelay;
                DateTime? scheduledFor = holdFor > TimeSpan.Zero
                    ? DateTime.SpecifyKind(DateTime.UtcNow.Add(holdFor), DateTimeKind.Unspecified)
                    : (DateTime?)null;
                var error = StoredError.From(lastException, nonRetryable);

                await using var retryCmd = new NpgsqlCommand($"""
                    UPDATE {_options.Schema}.workqueue
                    SET retrycount = retrycount + 1,
                        failedat = CASE WHEN @deadLetter THEN CURRENT_TIMESTAMP ELSE NULL END,
                        faultdispatchedat = NULL,
                        scheduledfor = @scheduledFor,
                        lasterror = @lastError
                    WHERE id = @id
                    """, conn, tran);
                retryCmd.Parameters.AddWithValue("id", msg.Id);
                retryCmd.Parameters.AddWithValue("deadLetter", willDeadLetter);
                retryCmd.Parameters.AddWithValue("lastError", error.ToJson());
                retryCmd.Parameters.Add(new NpgsqlParameter("scheduledFor", NpgsqlTypes.NpgsqlDbType.Timestamp)
                {
                    Value = scheduledFor.HasValue ? (object)scheduledFor.Value : DBNull.Value
                });
                await retryCmd.ExecuteNonQueryAsync();

                if (willDeadLetter)
                {
                    WorkQueueMetrics.RecordDeadLettered(Transport, msg.Channel, stopwatch.Elapsed);
                    faults.Add(new FaultInfo(msg.Channel, msg.Payload, msg.MessageId, attempt,
                        DateTimeOffset.UtcNow, error, IsRedelivery: false));
                }
                else
                {
                    WorkQueueMetrics.RecordRetried(Transport, msg.Channel, stopwatch.Elapsed);
                }
            }
        }

        await tran.CommitAsync();

        foreach (var fault in faults)
            await DeliverFaultAsync(fault);

        return messages.Count;
    }

    // Claims dead-lettered rows whose fault was never delivered and whose redelivery time has
    // passed. The claim pushes scheduledfor forward by FaultRedeliveryDelay and commits at once,
    // so no lock is held while fault consumers run; the pushed-forward time acts as a lease.
    private async Task<int> RedeliverFaultsAsync(int maxFaults)
    {
        var channelNames = _faults.Channels;
        if (channelNames.Length == 0) return 0;

        var sql = $"""
            UPDATE {_options.Schema}.workqueue
            SET scheduledfor = @leaseUntil
            WHERE id IN (
                SELECT id FROM {_options.Schema}.workqueue
                WHERE failedat IS NOT NULL
                  AND faultdispatchedat IS NULL
                  AND (scheduledfor IS NULL OR scheduledfor <= @now)
                  AND channel = ANY(@channels)
                ORDER BY failedat
                FOR UPDATE SKIP LOCKED
                LIMIT @maxFaults
            )
            RETURNING channel, payload, messageid, retrycount, failedat, lasterror
            """;

        var now = DateTime.UtcNow;
        var due = new List<FaultInfo>();
        await using (var conn = new NpgsqlConnection(_options.ConnectionString))
        {
            await conn.OpenAsync(_cts.Token);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@channels", channelNames);
            cmd.Parameters.AddWithValue("@maxFaults", maxFaults);
            cmd.Parameters.Add(new NpgsqlParameter("@now", NpgsqlTypes.NpgsqlDbType.Timestamp)
            {
                Value = DateTime.SpecifyKind(now, DateTimeKind.Unspecified)
            });
            cmd.Parameters.Add(new NpgsqlParameter("@leaseUntil", NpgsqlTypes.NpgsqlDbType.Timestamp)
            {
                Value = DateTime.SpecifyKind(now.Add(_options.FaultRedeliveryDelay), DateTimeKind.Unspecified)
            });

            await using var reader = await cmd.ExecuteReaderAsync(_cts.Token);
            while (await reader.ReadAsync(_cts.Token))
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
        }

        foreach (var fault in due)
            await DeliverFaultAsync(fault);

        return due.Count;
    }

    // Delivers a fault and records it as delivered. A fault consumer that throws leaves the row
    // pending; it is redelivered once scheduledfor passes.
    private async Task DeliverFaultAsync(FaultInfo fault)
    {
        bool delivered;
        try { delivered = await _faults.DispatchAsync(fault, _cts.Token); }
        catch (Exception fex)
        {
            _logger.LogError(fex, "Fault consumer threw for dead-lettered message {MessageId} on channel {Channel}; it will be redelivered",
                fault.MessageId, fault.Channel);
            return;
        }

        if (!delivered) return;

        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            UPDATE {_options.Schema}.workqueue
            SET faultdispatchedat = CURRENT_TIMESTAMP
            WHERE channel = @channel AND messageid = @messageid AND failedat IS NOT NULL
            """, conn);
        cmd.Parameters.AddWithValue("@channel", fault.Channel);
        cmd.Parameters.AddWithValue("@messageid", fault.MessageId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DispatchMessageAsync(MessageDto msg)
    {
        var type = _handlers.Keys.FirstOrDefault(t => t.FullName == msg.Channel);
        if (type == null)
        {
            _logger.LogWarning("No handlers registered for channel: {Channel} — message {Id} will be skipped", msg.Channel, msg.Id);
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
