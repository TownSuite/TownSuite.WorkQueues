using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TownSuite.WorkQueues.Redis;

/// <summary>
/// Redis Streams-backed message bus with at-least-once delivery, consumer groups,
/// automatic retry, scheduled delivery and dead-lettering.
/// </summary>
/// <remarks>
/// <para>Keys per message type, under <c>{KeyPrefix}:stream:{type}</c>: the stream itself, <c>:dead</c>
/// (dead-letter stream), <c>:scheduled</c> (sorted set of messages waiting for their delivery or retry
/// time), <c>:faults</c> and <c>:faultdata</c> (faults not yet delivered). <c>{KeyPrefix}:channels</c>
/// lists known streams for <see cref="PurgeProcessed"/>.</para>
/// <para>A failed message is retried after <see cref="RedisOptions.ReclaimIdleTime"/> when
/// <see cref="BatchOptions.RetryDelay"/> is zero. With a retry delay it is moved to the scheduled set and
/// re-added to the stream when the delay (with <see cref="BatchOptions.RetryBackoffMultiplier"/>) elapses.</para>
/// </remarks>
public class RedisMessageBus : MessageBusBase
{
    private const int PurgeBatchSize = 500;
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;
    private readonly HashSet<string> _groupsEnsured = new();

    public RedisMessageBus(IConnectionMultiplexer redis, RedisOptions options, ILogger logger,
        IServiceProvider? serviceProvider = null)
        : base(options, logger, serviceProvider, "redis")
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _options = options;
        StartPolling();
    }

    private IDatabase Db => _redis.GetDatabase();
    private string StreamKey(string channel) => $"{_options.KeyPrefix}:stream:{channel}";
    private string ChannelsKey => $"{_options.KeyPrefix}:channels";
    private string RetryHashKey => $"{_options.KeyPrefix}:retries";
    private static string DeadKey(string streamKey) => $"{streamKey}:dead";
    private static string ScheduledKey(string streamKey) => $"{streamKey}:scheduled";
    private static string FaultsKey(string streamKey) => $"{streamKey}:faults";
    private static string FaultDataKey(string streamKey) => $"{streamKey}:faultdata";

    // ── Publishing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Queues a publish on the caller's Redis <paramref name="transaction"/> (MULTI/EXEC), so the
    /// message is added only if the caller's transaction executes. This is the Redis counterpart of
    /// the SQL transports' transactional publish.
    /// </summary>
    /// <returns>The message id the message will have.</returns>
    public Guid Publish<T>(T message, ITransaction transaction, PublishOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var channel = ChannelName<T>();
        var messageId = Guid.NewGuid();
        QueueInsert(transaction, channel, JsonSerializer.Serialize(message), messageId, options ?? new PublishOptions());
        RecordPublished(channel);
        return messageId;
    }

    private protected override async Task InsertAsync(string channel, string payload, Guid messageId,
        PublishOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tran = Db.CreateTransaction();
        QueueInsert(tran, channel, payload, messageId, options);
        await tran.ExecuteAsync();
    }

    private void QueueInsert(ITransaction tran, string channel, string payload, Guid messageId, PublishOptions options)
    {
        var streamKey = StreamKey(channel);
        var now = DateTimeOffset.UtcNow;
        _ = tran.SetAddAsync(ChannelsKey, channel);

        var envelope = new Envelope(payload, messageId.ToString(), now.ToUnixTimeMilliseconds(),
            options.ExpiresAt?.ToUnixTimeMilliseconds(), 0);

        if (options.DeliverAfter is { } deliverAfter && deliverAfter > now)
            _ = tran.SortedSetAddAsync(ScheduledKey(streamKey), envelope.ToJson(), deliverAfter.ToUnixTimeMilliseconds());
        else
            _ = tran.StreamAddAsync(streamKey, envelope.ToFields());
    }

    // The fields a message carries on the stream, also stored in the scheduled set as JSON.
    private sealed record Envelope(
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("messageid")] string MessageId,
        [property: JsonPropertyName("sentat")] long SentAtMs,
        [property: JsonPropertyName("expiresat")] long? ExpiresAtMs,
        [property: JsonPropertyName("attempts")] int Attempts)
    {
        public string ToJson() => JsonSerializer.Serialize(this);

        public NameValueEntry[] ToFields()
        {
            var fields = new List<NameValueEntry>
            {
                new("payload", Payload), new("messageid", MessageId), new("sentat", SentAtMs)
            };
            if (ExpiresAtMs.HasValue) fields.Add(new("expiresat", ExpiresAtMs.Value));
            if (Attempts > 0) fields.Add(new("attempts", Attempts));
            return fields.ToArray();
        }

        public static Envelope From(StreamEntry entry, int attempts) => new(
            FieldOf(entry, "payload") ?? string.Empty,
            FieldOf(entry, "messageid") ?? string.Empty,
            LongField(entry, "sentat") ?? TimestampOf(entry.Id)?.ToUnixTimeMilliseconds() ?? 0,
            LongField(entry, "expiresat"),
            attempts);
    }

    // ── Claiming ────────────────────────────────────────────────────────────

    // Moves due entries from the scheduled set onto the stream, atomically.
    private const string MoveDueScheduledScript = """
        local due = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, tonumber(ARGV[2]))
        for _, member in ipairs(due) do
            local m = cjson.decode(member)
            local fields = { 'payload', m.payload, 'messageid', m.messageid, 'sentat', tostring(m.sentat) }
            if m.expiresat and m.expiresat ~= cjson.null then
                table.insert(fields, 'expiresat'); table.insert(fields, tostring(m.expiresat))
            end
            if m.attempts and m.attempts > 0 then
                table.insert(fields, 'attempts'); table.insert(fields, tostring(m.attempts))
            end
            redis.call('XADD', KEYS[2], '*', unpack(fields))
            redis.call('ZREM', KEYS[1], member)
        end
        return #due
        """;

    private protected override async Task<ClaimedBatch> ClaimAsync(string[] channels, int maxMessages, CancellationToken cancellationToken)
    {
        var db = Db;
        var messages = new List<MessageDto>();
        var deadLettered = new List<FaultInfo>();

        foreach (var channel in channels)
        {
            var streamKey = StreamKey(channel);
            await EnsureConsumerGroupAsync(db, channel, streamKey);

            await db.ScriptEvaluateAsync(MoveDueScheduledScript,
                new RedisKey[] { ScheduledKey(streamKey), streamKey },
                new RedisValue[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), maxMessages });

            // New, undelivered messages.
            var newEntries = await db.StreamReadGroupAsync(streamKey, _options.ConsumerGroup, _options.ConsumerName,
                ">", count: maxMessages);
            foreach (var entry in newEntries)
                messages.Add(ToMessage(channel, streamKey, entry, AttemptsOf(entry)));

            // Stale pending messages: delivered earlier but never acknowledged (retries).
            StreamAutoClaimResult claimed;
            try
            {
                claimed = await db.StreamAutoClaimAsync(streamKey, _options.ConsumerGroup, _options.ConsumerName,
                    _options.ReclaimIdleTimeMs, "0-0", count: maxMessages);
            }
            catch (RedisServerException)
            {
                continue; // XAUTOCLAIM unavailable (Redis < 6.2) — no reclaim-based retry
            }

            foreach (var entry in claimed.ClaimedEntries)
            {
                var failures = AttemptsOf(entry) + (int)await db.HashIncrementAsync(RetryHashKey, entry.Id.ToString());
                if (failures >= _options.MaxRetries)
                {
                    // The original exception is not retained across reclaim cycles.
                    var error = StoredError.From(new InvalidOperationException(
                        $"Dead-lettered after {failures} delivery attempts on stream '{streamKey}'."), nonRetryable: false);
                    var msg = ToMessage(channel, streamKey, entry, failures);
                    await DeadLetterAsync(db, msg, failures, error,
                        DateTimeOffset.UtcNow.Add(_options.FaultRedeliveryDelay));
                    WorkQueueMetrics.RecordDeadLettered("redis", channel, null);
                    deadLettered.Add(new FaultInfo(channel, msg.Payload, msg.MessageId, failures,
                        DateTimeOffset.UtcNow, error, IsRedelivery: false) { FaultKey = msg.FaultKey });
                    continue;
                }
                messages.Add(ToMessage(channel, streamKey, entry, failures));
            }
        }

        return new Batch(this, db, messages, deadLettered);
    }

    private async Task EnsureConsumerGroupAsync(IDatabase db, string channel, string streamKey)
    {
        lock (_groupsEnsured)
            if (_groupsEnsured.Contains(streamKey)) return;

        try
        {
            await db.StreamCreateConsumerGroupAsync(streamKey, _options.ConsumerGroup, StreamPosition.Beginning,
                createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP"))
        {
            // Group already exists — this is normal on restart
        }
        await db.SetAddAsync(ChannelsKey, channel);

        lock (_groupsEnsured)
            _groupsEnsured.Add(streamKey);
    }

    private static MessageDto ToMessage(string channel, string streamKey, StreamEntry entry, int failures)
    {
        var sentAt = LongField(entry, "sentat") is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : TimestampOf(entry.Id) ?? DateTimeOffset.UtcNow;

        return new MessageDto
        {
            Channel        = channel,
            Payload        = FieldOf(entry, "payload") ?? string.Empty,
            MessageId      = MessageIdOf(entry) ?? Guid.Empty,
            TimeCreatedUtc = sentAt.UtcDateTime,
            RetryCount     = failures,
            ExpiresAtUtc   = LongField(entry, "expiresat") is { } exp
                ? DateTimeOffset.FromUnixTimeMilliseconds(exp).UtcDateTime
                : null,
            Handle         = (streamKey, entry),
            FaultKey       = FaultKeyOf(entry)
        };
    }

    private sealed class Batch : ClaimedBatch
    {
        private readonly RedisMessageBus _bus;
        private readonly IDatabase _db;

        public Batch(RedisMessageBus bus, IDatabase db, List<MessageDto> messages, List<FaultInfo> deadLettered)
        {
            _bus = bus;
            _db = db;
            Messages = messages;
            DeadLetteredDuringClaim = deadLettered;
        }

        public override async Task CompleteAsync(MessageDto message)
        {
            var (streamKey, entry) = ((string, StreamEntry))message.Handle!;
            var tran = _db.CreateTransaction();
            _ = tran.StreamAcknowledgeAsync(streamKey, _bus._options.ConsumerGroup, entry.Id);
            _ = tran.HashDeleteAsync(_bus.RetryHashKey, entry.Id.ToString());
            await tran.ExecuteAsync();
        }

        public override async Task FailAsync(MessageDto message, FailureOutcome outcome)
        {
            var (streamKey, entry) = ((string, StreamEntry))message.Handle!;

            if (outcome.DeadLetter)
            {
                await _bus.DeadLetterAsync(_db, message, outcome.Attempt, outcome.Error,
                    outcome.ScheduledForUtc is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : DateTimeOffset.UtcNow);
            }
            else if (outcome.ScheduledForUtc is { } retryAt)
            {
                // Retry delay: move the message to the scheduled set; it returns to the stream when due.
                var tran = _db.CreateTransaction();
                _ = tran.SortedSetAddAsync(ScheduledKey(streamKey), Envelope.From(entry, outcome.Attempt).ToJson(),
                    new DateTimeOffset(retryAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
                _ = tran.StreamAcknowledgeAsync(streamKey, _bus._options.ConsumerGroup, entry.Id);
                _ = tran.HashDeleteAsync(_bus.RetryHashKey, entry.Id.ToString());
                await tran.ExecuteAsync();
            }
            // Otherwise the entry stays in the pending entry list and XAUTOCLAIM retries it
            // after ReclaimIdleTime.
        }
    }

    // Moves the entry to the dead-letter stream and records a pending fault, in one MULTI/EXEC.
    // The fault is delivered by the base class once outcomes are recorded; if that does not
    // happen it is redelivered from the pending set after FaultRedeliveryDelay.
    private async Task DeadLetterAsync(IDatabase db, MessageDto message, int attempts, StoredError error, DateTimeOffset faultDueAt)
    {
        var (streamKey, entry) = ((string, StreamEntry))message.Handle!;
        var now = DateTimeOffset.UtcNow;
        var faultKey = message.FaultKey ?? message.MessageId.ToString();

        var deadFields = new List<NameValueEntry>
        {
            new("payload", message.Payload),
            new("sentat", new DateTimeOffset(DateTime.SpecifyKind(message.TimeCreatedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()),
            new("failedat", now.ToUnixTimeMilliseconds()),
            new("attempts", attempts),
            new("lasterror", error.ToJson())
        };
        if (FieldOf(entry, "messageid") is { } id) deadFields.Add(new("messageid", id));

        var record = new FaultRecord(message.Payload, message.MessageId, attempts, now.ToUnixTimeMilliseconds(), error.ToJson());

        var tran = db.CreateTransaction();
        _ = tran.StreamAddAsync(DeadKey(streamKey), deadFields.ToArray());
        _ = tran.StreamAcknowledgeAsync(streamKey, _options.ConsumerGroup, entry.Id);
        _ = tran.HashDeleteAsync(RetryHashKey, entry.Id.ToString());
        _ = tran.HashSetAsync(FaultDataKey(streamKey), faultKey, JsonSerializer.Serialize(record));
        _ = tran.SortedSetAddAsync(FaultsKey(streamKey), faultKey, faultDueAt.ToUnixTimeMilliseconds());
        await tran.ExecuteAsync();

        Logger.LogWarning("Message {Id} dead-lettered on stream {Stream} after {Count} attempts",
            entry.Id, streamKey, attempts);
    }

    private sealed record FaultRecord(string Payload, Guid MessageId, int Attempts, long FailedAtMs, string? LastError);

    // ── Faults ──────────────────────────────────────────────────────────────

    // Atomically takes the due pending faults and pushes their next attempt forward by the
    // lease, so concurrent buses do not deliver the same fault at once.
    private const string ClaimDueFaultsScript = """
        local due = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, tonumber(ARGV[3]))
        for _, member in ipairs(due) do
            redis.call('ZADD', KEYS[1], ARGV[2], member)
        end
        return due
        """;

    private protected override async Task<IReadOnlyList<FaultInfo>> ClaimDueFaultsAsync(string[] channels, int maxFaults,
        TimeSpan lease, CancellationToken cancellationToken)
    {
        var db = Db;
        var due = new List<FaultInfo>();
        foreach (var channel in channels)
        {
            var streamKey = StreamKey(channel);
            var now = DateTimeOffset.UtcNow;
            var claimed = (RedisResult[]?)await db.ScriptEvaluateAsync(ClaimDueFaultsScript,
                new RedisKey[] { FaultsKey(streamKey) },
                new RedisValue[] { now.ToUnixTimeMilliseconds(), now.Add(lease).ToUnixTimeMilliseconds(), maxFaults });
            if (claimed == null) continue;

            foreach (var member in claimed)
            {
                var faultKey = member.ToString()!;
                var json = await db.HashGetAsync(FaultDataKey(streamKey), faultKey);
                if (json.IsNull)
                {
                    await db.SortedSetRemoveAsync(FaultsKey(streamKey), faultKey);
                    continue;
                }

                var record = JsonSerializer.Deserialize<FaultRecord>(json.ToString())!;
                due.Add(new FaultInfo(channel, record.Payload, record.MessageId, record.Attempts,
                    DateTimeOffset.FromUnixTimeMilliseconds(record.FailedAtMs), StoredError.Parse(record.LastError),
                    IsRedelivery: true) { FaultKey = faultKey });
            }
        }
        return due;
    }

    private protected override async Task MarkFaultDeliveredAsync(FaultInfo fault)
    {
        var streamKey = StreamKey(fault.Channel);
        var faultKey = fault.FaultKey ?? fault.MessageId.ToString();
        var tran = Db.CreateTransaction();
        _ = tran.SortedSetRemoveAsync(FaultsKey(streamKey), faultKey);
        _ = tran.HashDeleteAsync(FaultDataKey(streamKey), faultKey);
        await tran.ExecuteAsync();
    }

    // ── Dead-letters ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default)
    {
        var streamKey = StreamKey(ChannelName<T>());
        var db = Db;

        int replayed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await db.StreamRangeAsync(DeadKey(streamKey), "-", "+", count: 100);
            if (entries == null || entries.Length == 0) break;

            foreach (var entry in entries)
            {
                await db.StreamAddAsync(streamKey, ReplayFields(entry));
                await db.StreamDeleteAsync(DeadKey(streamKey), new[] { entry.Id });
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
    public override async Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default)
    {
        var streamKey = StreamKey(ChannelName<T>());
        var db = Db;
        var target = messageId.ToString();

        RedisValue? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await db.StreamRangeAsync(DeadKey(streamKey),
                after.HasValue ? $"({after.Value}" : "-", "+", count: 100);
            if (entries == null || entries.Length == 0) return false;

            foreach (var entry in entries)
            {
                if (MessageIdOf(entry) is { } id && id.ToString() == target)
                {
                    var tran = db.CreateTransaction();
                    _ = tran.StreamAddAsync(streamKey, ReplayFields(entry));
                    _ = tran.StreamDeleteAsync(DeadKey(streamKey), new[] { entry.Id });
                    _ = tran.SortedSetRemoveAsync(FaultsKey(streamKey), target);
                    _ = tran.HashDeleteAsync(FaultDataKey(streamKey), target);
                    await tran.ExecuteAsync();
                    return true;
                }
            }

            if (entries.Length < 100) return false;
            after = entries[^1].Id;
        }
    }

    // A replayed message starts again: original payload, id and send time, no attempts or expiry.
    private static NameValueEntry[] ReplayFields(StreamEntry entry) =>
        entry.Values.Where(v => v.Name == "payload" || v.Name == "messageid" || v.Name == "sentat").ToArray();

    /// <inheritdoc />
    public override async Task<IReadOnlyList<DeadLetteredMessage<T>>> GetDeadLettered<T>(int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        var streamKey = StreamKey(ChannelName<T>());
        var db = Db;
        skip = Math.Max(0, skip);
        take = Math.Max(0, take);
        if (take == 0) return Array.Empty<DeadLetteredMessage<T>>();

        var entries = await db.StreamRangeAsync(DeadKey(streamKey), "-", "+",
            count: skip + take, messageOrder: Order.Descending);

        var result = new List<DeadLetteredMessage<T>>();
        foreach (var entry in entries.Skip(skip))
        {
            var deadAt = TimestampOf(entry.Id) ?? DateTimeOffset.UtcNow;
            var pendingFault = await db.SortedSetScoreAsync(FaultsKey(streamKey), FaultKeyOf(entry));

            result.Add(FaultRegistry.ToDeadLettered<T>(
                MessageIdOf(entry) ?? Guid.Empty,
                FieldOf(entry, "payload") ?? string.Empty,
                LongField(entry, "sentat") is { } sent ? DateTimeOffset.FromUnixTimeMilliseconds(sent) : deadAt,
                LongField(entry, "failedat") is { } failed ? DateTimeOffset.FromUnixTimeMilliseconds(failed) : deadAt,
                (int)(LongField(entry, "attempts") ?? 0),
                FieldOf(entry, "lasterror"),
                faultDelivered: pendingFault == null));
        }
        return result;
    }

    // ── Statistics ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="QueueStatistics.PendingCount"/> is the consumer group's lag (entries not yet
    /// delivered), its pending entry list (delivered but not acknowledged) and the scheduled set.
    /// Lag requires Redis 7.0+; on older servers it is not counted.
    /// </remarks>
    public override async Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default)
    {
        var channel = ChannelName<T>();
        var streamKey = StreamKey(channel);
        var db = Db;
        await EnsureConsumerGroupAsync(db, channel, streamKey);

        var groups = await db.StreamGroupInfoAsync(streamKey);
        var group = groups.FirstOrDefault(g => g.Name == _options.ConsumerGroup);

        // Oldest waiting entry: the lowest pending (unacknowledged) id, or the first entry after
        // the group's last-delivered id, whichever is older.
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
            PendingCount      = pending.PendingMessageCount + (group.Lag ?? 0) + await db.SortedSetLengthAsync(ScheduledKey(streamKey)),
            DeadLetteredCount = await db.StreamLengthAsync(DeadKey(streamKey)),
            PendingFaultCount = await db.SortedSetLengthAsync(FaultsKey(streamKey)),
            OldestReadySince  = oldest,
            CapturedAt        = DateTimeOffset.UtcNow
        };
    }

    // ── Purging ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Trims each known stream (<c>{KeyPrefix}:channels</c>) up to the oldest entry that some
    /// consumer group has not yet acknowledged or read, and no further than
    /// <paramref name="processedBefore"/>. Entries are trimmed by publish time, which is when Redis
    /// assigned their id.
    /// </remarks>
    public override async Task<long> PurgeProcessed(DateTimeOffset processedBefore, CancellationToken cancellationToken = default)
    {
        var db = Db;
        long total = 0;

        foreach (var channel in await db.SetMembersAsync(ChannelsKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var streamKey = StreamKey(channel.ToString());
            if (!await db.KeyExistsAsync(streamKey)) continue;

            // Never trim past what any consumer group still needs.
            var minId = $"{processedBefore.ToUnixTimeMilliseconds()}-0";
            foreach (var group in await db.StreamGroupInfoAsync(streamKey))
            {
                var pending = await db.StreamPendingAsync(streamKey, group.Name);
                if (pending.PendingMessageCount > 0)
                    minId = MinId(minId, pending.LowestPendingMessageId.ToString());

                var nothingDelivered = string.IsNullOrEmpty(group.LastDeliveredId) || group.LastDeliveredId == "0-0";
                var unread = await db.StreamRangeAsync(streamKey, nothingDelivered ? "-" : $"({group.LastDeliveredId}", "+", count: 1);
                if (unread.Length > 0)
                    minId = MinId(minId, unread[0].Id.ToString());
            }

            var trimmed = await db.ExecuteAsync("XTRIM", streamKey, "MINID", minId);
            total += (long)trimmed;
        }
        return total;
    }

    /// <inheritdoc />
    public override async Task<long> PurgeDeadLettered<T>(DateTimeOffset failedBefore, CancellationToken cancellationToken = default)
    {
        var streamKey = StreamKey(ChannelName<T>());
        var db = Db;
        var before = failedBefore.ToUnixTimeMilliseconds();
        long total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Dead-letter entry ids are assigned when the message is dead-lettered.
            var entries = await db.StreamRangeAsync(DeadKey(streamKey), "-", $"({before}-0", count: PurgeBatchSize);
            if (entries.Length == 0) return total;

            var tran = db.CreateTransaction();
            _ = tran.StreamDeleteAsync(DeadKey(streamKey), entries.Select(e => e.Id).ToArray());
            var keys = entries.Select(e => (RedisValue)FaultKeyOf(e)).ToArray();
            _ = tran.SortedSetRemoveAsync(FaultsKey(streamKey), keys);
            _ = tran.HashDeleteAsync(FaultDataKey(streamKey), keys);
            await tran.ExecuteAsync();

            total += entries.Length;
            if (entries.Length < PurgeBatchSize) return total;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // Stream entry ids are "{epochMs}-{seq}".
    private static DateTimeOffset? TimestampOf(RedisValue entryId)
    {
        var idParts = entryId.ToString().Split('-');
        return idParts.Length > 0 && long.TryParse(idParts[0], out var epochMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            : null;
    }

    private static string MinId(string a, string b) => CompareIds(a, b) <= 0 ? a : b;

    private static int CompareIds(string a, string b)
    {
        static (long Ms, long Seq) Parse(string id)
        {
            var parts = id.Split('-');
            return (long.Parse(parts[0]), parts.Length > 1 ? long.Parse(parts[1]) : 0);
        }
        return Parse(a).CompareTo(Parse(b));
    }

    private static Guid? MessageIdOf(StreamEntry entry) =>
        Guid.TryParse(FieldOf(entry, "messageid"), out var g) ? g : null;

    // The key a pending fault is stored under: the message id, or the entry id for entries
    // published before message ids were recorded.
    private static string FaultKeyOf(StreamEntry entry) =>
        MessageIdOf(entry)?.ToString() ?? $"entry:{entry.Id}";

    private static int AttemptsOf(StreamEntry entry) => (int)(LongField(entry, "attempts") ?? 0);

    private static string? FieldOf(StreamEntry entry, string name)
    {
        var field = entry.Values.FirstOrDefault(v => v.Name == name);
        return field.Value.IsNull ? null : field.Value.ToString();
    }

    private static long? LongField(StreamEntry entry, string name) =>
        long.TryParse(FieldOf(entry, name), out var value) ? value : null;
}
