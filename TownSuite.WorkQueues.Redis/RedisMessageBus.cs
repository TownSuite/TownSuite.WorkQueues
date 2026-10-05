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
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<object, Task>>> _faultHandlers = new();
    private readonly ConcurrentDictionary<Type, Func<string, Exception, int, Guid, bool, Task>> _faultDispatchers = new();
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
                await db.StreamAddAsync(streamKey, entry.Values);
                await db.StreamDeleteAsync(deadKey, new[] { entry.Id });
                replayed++;
            }

            if (entries.Length < 100) break;
        }

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
                    await db.StreamAddAsync(streamKey, entry.Values);
                    await db.StreamDeleteAsync(deadKey, new[] { entry.Id });
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
            OldestReadySince  = oldest,
            CapturedAt        = DateTimeOffset.UtcNow
        };
    }

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

    // Copies the entry to the dead-letter stream, ACKs it on the main stream, then notifies
    // fault consumers.
    private async Task DeadLetterAsync(IDatabase db, string streamKey, string typeName, StreamEntry entry,
        string retryHashKey, int attemptCount, Exception exception, bool nonRetryable, TimeSpan? elapsed)
    {
        await db.StreamAddAsync($"{streamKey}:dead", entry.Values);
        await db.StreamAcknowledgeAsync(streamKey, _options.ConsumerGroup, entry.Id);
        await db.HashDeleteAsync(retryHashKey, entry.Id.ToString());
        WorkQueueMetrics.RecordDeadLettered(Transport, typeName, elapsed);

        var msgType = _handlers.Keys.FirstOrDefault(t => t.FullName == typeName);
        if (msgType != null && _faultDispatchers.TryGetValue(msgType, out var faultDispatcher))
        {
            var payloadField = entry.Values.FirstOrDefault(v => v.Name == "payload");
            if (!payloadField.Value.IsNull)
            {
                try
                {
                    await faultDispatcher(payloadField.Value.ToString() ?? string.Empty, exception, attemptCount,
                        MessageIdOf(entry) ?? Guid.Empty, nonRetryable);
                }
                catch (Exception fex)
                {
                    _logger.LogError(fex, "Fault consumer threw for dead-lettered entry {Id}", entry.Id);
                }
            }
        }

        _logger.LogWarning(
            "Message {Id} dead-lettered on stream {Stream} after {Count} attempts",
            entry.Id, streamKey, attemptCount);
    }

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
