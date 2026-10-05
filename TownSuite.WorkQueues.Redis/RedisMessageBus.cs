using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace TownSuite.WorkQueues.Redis;

/// <summary>
/// Redis Streams-backed message bus with at-least-once delivery, consumer groups,
/// automatic retry, and dead-lettering.
/// </summary>
public class RedisMessageBus : IMessageBus
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>>> _handlers = new();
    private readonly FaultRegistry _faults = new();
    private readonly IntervalGate _faultGate;
    private readonly ConcurrentDictionary<string, bool> _groupsEnsured = new();
    private readonly Task[] _pollingTasks;
    private const string Transport = "redis";
    private readonly ILogger _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;
    private readonly IServiceProvider? _serviceProvider;

    public RedisMessageBus(IConnectionMultiplexer redis, RedisOptions options, ILogger logger,
        IServiceProvider? serviceProvider = null)
    {
        _redis = redis   ?? throw new ArgumentNullException(nameof(redis));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    /// constructor (automatically supplied by
    /// <see cref="RedisServiceExtensions.AddRedisMessageBus"/>).
    /// </summary>
    public void Subscribe<TMessage, TConsumer>() where TConsumer : class, IConsumer<TMessage>
    {
        if (_serviceProvider == null)
            throw new InvalidOperationException(
                "Scoped consumer registration requires IServiceProvider. " +
                "Pass serviceProvider to the RedisMessageBus constructor, " +
                "or use the AddRedisMessageBus DI extension.");

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
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        if (channel.Length > 500)
            throw new ArgumentException($"Message type name exceeds 500 characters: {channel}");

        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(message);
        var messageId = Guid.NewGuid().ToString();
        var db = _redis.GetDatabase();
        await db.StreamAddAsync(StreamKey(channel),
            new[] { new NameValueEntry("payload", payload), new NameValueEntry("messageid", messageId) });
        WorkQueueMetrics.RecordPublished(Transport, channel);
    }

    /// <summary>
    /// Scheduled/delayed delivery is not supported by the Redis transport.
    /// Use the Postgres or SQL Server transport for this feature.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public Task Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "The Redis transport does not support scheduled/delayed delivery. " +
            "Use the Postgres or SQL Server transport, or implement a scheduler service that publishes at the target time.");

    /// <inheritdoc />
    public async Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default)
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        var streamKey = StreamKey(channel);
        var deadKey = $"{streamKey}:dead";
        var db = _redis.GetDatabase();

        int replayed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await db.StreamReadAsync(deadKey, "0-0", count: 100);
            if (entries == null || entries.Length == 0) break;

            foreach (var entry in entries)
            {
                await db.StreamAddAsync(streamKey, OriginalFields(entry));
                await db.StreamDeleteAsync(deadKey, new[] { entry.Id });
                replayed++;
            }

            if (entries.Length < 100) break;
        }

        // Replayed messages are no longer dead-lettered, so their pending faults are dropped.
        await db.KeyDeleteAsync(new RedisKey[] { FaultsKey(streamKey), FaultDataKey(streamKey) });
        return replayed;
    }

    /// <inheritdoc />
    /// <remarks>Scans the dead-letter stream for the entry whose <c>messageid</c> field matches.</remarks>
    public async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        var streamKey = StreamKey(channel);
        var deadKey = $"{streamKey}:dead";
        var db = _redis.GetDatabase();
        var target = messageId.ToString();

        RedisValue? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await db.StreamRangeAsync(deadKey,
                after.HasValue ? $"({after.Value}" : "-", "+", count: 100);
            if (entries == null || entries.Length == 0) return false;

            foreach (var entry in entries)
            {
                if (MessageIdOf(entry) is { } id && id.ToString() == target)
                {
                    await db.StreamAddAsync(streamKey, OriginalFields(entry));
                    await db.StreamDeleteAsync(deadKey, new[] { entry.Id });
                    await db.SortedSetRemoveAsync(FaultsKey(streamKey), target);
                    await db.HashDeleteAsync(FaultDataKey(streamKey), target);
                    return true;
                }
            }

            if (entries.Length < 100) return false;
            after = entries[^1].Id;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="QueueStatistics.PendingCount"/> is the consumer group's lag (entries not yet
    /// delivered) plus its pending entry list (delivered but not acknowledged). Lag requires Redis 7.0+;
    /// on older servers only the pending entry list is counted.
    /// </remarks>
    public async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        var streamKey = StreamKey(channel);
        var db = _redis.GetDatabase();
        await EnsureConsumerGroupAsync(db, streamKey);

        var groups = await db.StreamGroupInfoAsync(streamKey);
        var group = groups.FirstOrDefault(g => g.Name == _options.ConsumerGroup);
        var deadCount = await db.StreamLengthAsync($"{streamKey}:dead");

        // Oldest waiting entry: the lowest pending (unacknowledged) id, or the first entry
        // after the group's last-delivered id, whichever is older.
        DateTimeOffset? oldest = null;
        var pending = await db.StreamPendingAsync(streamKey, _options.ConsumerGroup);
        if (pending.PendingMessageCount > 0)
            oldest = TimestampOf(pending.LowestPendingMessageId);

        var nothingDelivered = string.IsNullOrEmpty(group.LastDeliveredId) || group.LastDeliveredId == "0-0";
        var undelivered = await db.StreamRangeAsync(streamKey,
            nothingDelivered ? "-" : $"({group.LastDeliveredId}", "+", count: 1);
        if (undelivered.Length > 0 && TimestampOf(undelivered[0].Id) is { } firstUndelivered
            && (oldest == null || firstUndelivered < oldest))
            oldest = firstUndelivered;

        return new QueueStatistics
        {
            Channel           = channel,
            PendingCount      = pending.PendingMessageCount + (group.Lag ?? 0),
            DeadLetteredCount = deadCount,
            PendingFaultCount = await db.SortedSetLengthAsync(FaultsKey(streamKey)),
            OldestReadySince  = oldest,
            CapturedAt        = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetteredMessage<T>>> GetDeadLettered<T>(int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        var streamKey = StreamKey(channel);
        var db = _redis.GetDatabase();
        skip = Math.Max(0, skip);
        take = Math.Max(0, take);
        if (take == 0) return Array.Empty<DeadLetteredMessage<T>>();

        var entries = await db.StreamRangeAsync($"{streamKey}:dead", "-", "+",
            count: skip + take, messageOrder: Order.Descending);

        var result = new List<DeadLetteredMessage<T>>();
        foreach (var entry in entries.Skip(skip))
        {
            var payload = FieldOf(entry, "payload") ?? string.Empty;
            var key = FaultKeyOf(entry);
            var deadAt = TimestampOf(entry.Id) ?? DateTimeOffset.UtcNow;
            var pendingFault = await db.SortedSetScoreAsync(FaultsKey(streamKey), key);

            result.Add(FaultRegistry.ToDeadLettered<T>(
                MessageIdOf(entry) ?? Guid.Empty,
                payload,
                MillisecondsField(entry, "sentat") ?? deadAt,
                MillisecondsField(entry, "failedat") ?? deadAt,
                int.TryParse(FieldOf(entry, "attempts"), out var attempts) ? attempts : 0,
                FieldOf(entry, "lasterror"),
                faultDelivered: pendingFault == null));
        }
        return result;
    }

    private static string FaultsKey(string streamKey) => $"{streamKey}:faults";
    private static string FaultDataKey(string streamKey) => $"{streamKey}:faultdata";

    // The key a pending fault is stored under: the message id, or the entry id for entries
    // published before message ids were recorded.
    private static string FaultKeyOf(StreamEntry entry) =>
        MessageIdOf(entry)?.ToString() ?? $"entry:{entry.Id}";

    private static string? FieldOf(StreamEntry entry, string name)
    {
        var field = entry.Values.FirstOrDefault(v => v.Name == name);
        return field.Value.IsNull ? null : field.Value.ToString();
    }

    private static DateTimeOffset? MillisecondsField(StreamEntry entry, string name) =>
        long.TryParse(FieldOf(entry, name), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    // The fields a message was published with, without the dead-letter details added to it.
    private static NameValueEntry[] OriginalFields(StreamEntry entry) =>
        entry.Values.Where(v => v.Name == "payload" || v.Name == "messageid").ToArray();

    // Stream entry ids are "{epochMs}-{seq}".
    private static DateTimeOffset? TimestampOf(RedisValue entryId)
    {
        var idParts = entryId.ToString().Split('-');
        return idParts.Length > 0 && long.TryParse(idParts[0], out var epochMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            : null;
    }

    private static Guid? MessageIdOf(StreamEntry entry)
    {
        var messageIdField = entry.Values.FirstOrDefault(v => v.Name == "messageid");
        return !messageIdField.Value.IsNull && Guid.TryParse(messageIdField.Value.ToString(), out var g) ? g : null;
    }

    private async Task ProcessMessagesAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                int processed = 0;
                var db = _redis.GetDatabase();

                foreach (var (type, _) in _handlers)
                {
                    var channel = type.FullName;
                    if (channel == null) continue;
                    processed += await ProcessStreamAsync(db, StreamKey(channel), channel);
                }

                if (_faultGate.TryEnter())
                {
                    foreach (var channel in _faults.Channels)
                        processed += await RedeliverFaultsAsync(db, StreamKey(channel), channel);
                }

                if (processed == 0)
                    await WaitAsync();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RedisMessageBus polling loop");
                try { await Task.Delay(1000, _cts.Token); } catch (TaskCanceledException) { }
            }
        }
    }

    private async Task WaitAsync()
    {
        if (!_options.ContinuousPolling)
            await Task.Delay(_options.MaxWaitTime, _cts.Token);
    }

    private async Task EnsureConsumerGroupAsync(IDatabase db, string streamKey)
    {
        if (_groupsEnsured.ContainsKey(streamKey)) return;

        try
        {
            await db.StreamCreateConsumerGroupAsync(
                streamKey,
                _options.ConsumerGroup,
                StreamPosition.Beginning,
                createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP"))
        {
            // Group already exists — this is normal on restart
        }

        _groupsEnsured.TryAdd(streamKey, true);
    }

    private async Task<int> ProcessStreamAsync(IDatabase db, string streamKey, string typeName)
    {
        await EnsureConsumerGroupAsync(db, streamKey);

        int processed = 0;

        // Claim new, undelivered messages
        var newEntries = await db.StreamReadGroupAsync(
            streamKey,
            _options.ConsumerGroup,
            _options.ConsumerName,
            ">",
            count: _options.MaxBatchSize);

        var retryHashKey = $"{_options.KeyPrefix}:retries";

        foreach (var entry in newEntries)
        {
            var stopwatch = Stopwatch.StartNew();
            var error = await DispatchEntryAsync(entry, typeName);
            stopwatch.Stop();
            if (error == null)
            {
                await db.StreamAcknowledgeAsync(streamKey, _options.ConsumerGroup, entry.Id);
                WorkQueueMetrics.RecordProcessed(Transport, typeName, stopwatch.Elapsed);
                processed++;
            }
            else if (!_options.ShouldRetry(error))
            {
                await DeadLetterAsync(db, streamKey, typeName, entry, retryHashKey, 1, error, true, stopwatch.Elapsed);
            }
            else
            {
                // Message stays in the Pending Entry List; XAUTOCLAIM reclaims it later
                WorkQueueMetrics.RecordRetried(Transport, typeName, stopwatch.Elapsed);
            }
        }

        // Reclaim stale pending messages (retries)
        StreamAutoClaimResult claimed;
        try
        {
            claimed = await db.StreamAutoClaimAsync(
                streamKey,
                _options.ConsumerGroup,
                _options.ConsumerName,
                _options.ReclaimIdleTimeMs,
                "0-0",
                count: _options.MaxBatchSize);
        }
        catch (RedisServerException)
        {
            // XAUTOCLAIM unavailable (Redis < 6.2) — skip retry logic
            return processed;
        }

        if (claimed.ClaimedEntries.Length == 0)
            return processed;

        foreach (var entry in claimed.ClaimedEntries)
        {
            var msgIdField = entry.Id.ToString();
            var retryCount = (int)await db.HashIncrementAsync(retryHashKey, msgIdField);

            if (retryCount >= _options.MaxRetries)
            {
                // The Redis transport does not retain the original exception across retry
                // cycles, so a synthetic exception is used for the fault notification.
                var synthEx = new InvalidOperationException(
                    $"Dead-lettered after {retryCount} delivery attempts on stream '{streamKey}'.");
                await DeadLetterAsync(db, streamKey, typeName, entry, retryHashKey, retryCount, synthEx, false, null);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var error = await DispatchEntryAsync(entry, typeName);
            stopwatch.Stop();
            if (error == null)
            {
                await db.StreamAcknowledgeAsync(streamKey, _options.ConsumerGroup, entry.Id);
                await db.HashDeleteAsync(retryHashKey, msgIdField);
                WorkQueueMetrics.RecordProcessed(Transport, typeName, stopwatch.Elapsed);
                processed++;
            }
            else if (!_options.ShouldRetry(error))
            {
                await DeadLetterAsync(db, streamKey, typeName, entry, retryHashKey, retryCount + 1, error, true, stopwatch.Elapsed);
            }
            else
            {
                WorkQueueMetrics.RecordRetried(Transport, typeName, stopwatch.Elapsed);
            }
        }

        return processed;
    }

    // Moves the entry to the dead-letter stream and records a pending fault in one MULTI/EXEC,
    // then delivers the fault. If delivery throws or the process stops first, the pending fault
    // is picked up by RedeliverFaultsAsync after FaultRedeliveryDelay.
    private async Task DeadLetterAsync(IDatabase db, string streamKey, string typeName, StreamEntry entry,
        string retryHashKey, int attemptCount, Exception exception, bool nonRetryable, TimeSpan? elapsed)
    {
        var now = DateTimeOffset.UtcNow;
        var error = StoredError.From(exception, nonRetryable);
        var payload = FieldOf(entry, "payload");
        var faultKey = FaultKeyOf(entry);
        var sentAt = TimestampOf(entry.Id) ?? now;

        var deadFields = OriginalFields(entry).Concat(new[]
        {
            new NameValueEntry("sentat", sentAt.ToUnixTimeMilliseconds()),
            new NameValueEntry("failedat", now.ToUnixTimeMilliseconds()),
            new NameValueEntry("attempts", attemptCount),
            new NameValueEntry("lasterror", error.ToJson())
        }).ToArray();

        var tran = db.CreateTransaction();
        _ = tran.StreamAddAsync($"{streamKey}:dead", deadFields);
        _ = tran.StreamAcknowledgeAsync(streamKey, _options.ConsumerGroup, entry.Id);
        _ = tran.HashDeleteAsync(retryHashKey, entry.Id.ToString());
        if (payload != null)
        {
            var record = new RedisFaultRecord(payload, MessageIdOf(entry) ?? Guid.Empty, attemptCount,
                now.ToUnixTimeMilliseconds(), error.ToJson());
            _ = tran.HashSetAsync(FaultDataKey(streamKey), faultKey, JsonSerializer.Serialize(record));
            _ = tran.SortedSetAddAsync(FaultsKey(streamKey), faultKey,
                now.Add(_options.FaultRedeliveryDelay).ToUnixTimeMilliseconds());
        }
        await tran.ExecuteAsync();

        WorkQueueMetrics.RecordDeadLettered(Transport, typeName, elapsed);
        _logger.LogWarning(
            "Message {Id} dead-lettered on stream {Stream} after {Count} attempts",
            entry.Id, streamKey, attemptCount);

        if (payload != null)
        {
            await DeliverFaultAsync(db, streamKey, faultKey, new FaultInfo(typeName, payload,
                MessageIdOf(entry) ?? Guid.Empty, attemptCount, now, error, IsRedelivery: false));
        }
    }

    // Atomically takes the due pending faults and pushes their next attempt forward by
    // FaultRedeliveryDelay, so concurrent buses do not deliver the same fault at once.
    private const string ClaimDueFaultsScript = """
        local due = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, tonumber(ARGV[3]))
        for _, member in ipairs(due) do
            redis.call('ZADD', KEYS[1], ARGV[2], member)
        end
        return due
        """;

    private async Task<int> RedeliverFaultsAsync(IDatabase db, string streamKey, string channel)
    {
        var now = DateTimeOffset.UtcNow;
        var claimed = (RedisResult[]?)await db.ScriptEvaluateAsync(ClaimDueFaultsScript,
            new RedisKey[] { FaultsKey(streamKey) },
            new RedisValue[]
            {
                now.ToUnixTimeMilliseconds(),
                now.Add(_options.FaultRedeliveryDelay).ToUnixTimeMilliseconds(),
                _options.MaxBatchSize
            });
        if (claimed == null || claimed.Length == 0) return 0;

        int delivered = 0;
        foreach (var member in claimed)
        {
            var faultKey = member.ToString()!;
            var json = await db.HashGetAsync(FaultDataKey(streamKey), faultKey);
            if (json.IsNull)
            {
                await db.SortedSetRemoveAsync(FaultsKey(streamKey), faultKey);
                continue;
            }

            var record = JsonSerializer.Deserialize<RedisFaultRecord>(json.ToString())!;
            await DeliverFaultAsync(db, streamKey, faultKey, new FaultInfo(channel, record.Payload,
                record.MessageId, record.Attempts, DateTimeOffset.FromUnixTimeMilliseconds(record.FailedAtMs),
                StoredError.Parse(record.LastError), IsRedelivery: true));
            delivered++;
        }
        return delivered;
    }

    // Delivers a fault and removes it from the pending set. A fault consumer that throws leaves
    // it pending; it is redelivered once its score passes.
    private async Task DeliverFaultAsync(IDatabase db, string streamKey, string faultKey, FaultInfo fault)
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

        var tran = db.CreateTransaction();
        _ = tran.SortedSetRemoveAsync(FaultsKey(streamKey), faultKey);
        _ = tran.HashDeleteAsync(FaultDataKey(streamKey), faultKey);
        await tran.ExecuteAsync();
    }

    private sealed record RedisFaultRecord(string Payload, Guid MessageId, int Attempts, long FailedAtMs, string? LastError);

    // Returns null on success (or when the entry should be ACK-ed and skipped), otherwise the
    // exception thrown by the consumers.
    private async Task<Exception?> DispatchEntryAsync(StreamEntry entry, string typeName)
    {
        try
        {
            var payloadField = entry.Values.FirstOrDefault(v => v.Name == "payload");
            if (payloadField.Value.IsNull)
            {
                _logger.LogWarning("Stream entry {Id} has no 'payload' field — ACK-ing to avoid infinite retry", entry.Id);
                return null;
            }

            var type = _handlers.Keys.FirstOrDefault(t => t.FullName == typeName);
            if (type == null || !_handlers.TryGetValue(type, out var handlers))
            {
                _logger.LogWarning("No handler registered for channel {Channel} — ACK-ing", typeName);
                return null;
            }

            // Extract the stable message ID stored on publish; fall back to Guid.Empty for older entries.
            var messageId = MessageIdOf(entry) ?? Guid.Empty;

            // Derive sentTime from the stream entry ID millisecond timestamp.
            var sentTime = TimestampOf(entry.Id) ?? DateTimeOffset.UtcNow;

            var message = LegacyJsonDeserializer.Deserialize((string)payloadField.Value!, type);
            if (message == null) return null;

            await Task.WhenAll(handlers.Values.Select(h => h(message, messageId, sentTime)));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handler failed for stream entry {Id} on channel {Channel}", entry.Id, typeName);
            return ex;
        }
    }

    private string StreamKey(string channel) => $"{_options.KeyPrefix}:stream:{channel}";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_pollingTasks).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        _cts.Dispose();
    }
}
