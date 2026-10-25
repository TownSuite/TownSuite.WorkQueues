using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TownSuite.WorkQueues.Server.Stores;

/// <summary>
/// Topics in Redis: one stream per topic and one consumer group per subscription, so a message is
/// written once and every group reads its own copy. Unacknowledged entries are reclaimed with
/// <c>XAUTOCLAIM</c> once idle for the claim lease; the pending entry list's delivery count is the
/// attempt number. The backend for higher volumes.
/// </summary>
/// <remarks>
/// Keys, under <c>{KeyPrefix}:topic:{topic}</c>: the stream itself, <c>:subs</c> (hash of subscription
/// expiry times), <c>:errors:{subscriber}</c> (last nack error per entry) and <c>:dead:{subscriber}</c>
/// (dead-letter stream). <c>{KeyPrefix}:topics</c> lists topics for the cleanup.
/// </remarks>
internal sealed partial class RedisTopicStore : ITopicStore
{
    private const string ConsumerName = "server";
    private readonly TopicServerOptions _options;
    private readonly ILogger _logger;
    private readonly IConnectionMultiplexer _redis;

    public RedisTopicStore(TopicServerOptions options, IConnectionMultiplexer redis, ILogger<RedisTopicStore> logger)
    {
        _options = options;
        _redis   = redis;
        _logger  = logger;
    }

    private IDatabase Db => _redis.GetDatabase();
    private string TopicsKey => $"{_options.KeyPrefix}:topics";
    private string StreamKey(string topic) => $"{_options.KeyPrefix}:topic:{topic}";
    private string SubsKey(string topic) => $"{StreamKey(topic)}:subs";
    private string ErrorsKey(string topic, string sub) => $"{StreamKey(topic)}:errors:{sub}";
    private string DeadKey(string topic, string sub) => $"{StreamKey(topic)}:dead:{sub}";

    // A receipt is "{entry id}.{delivery count}", so a receipt from an earlier delivery of a
    // redelivered entry no longer matches.
    [GeneratedRegex(@"^(\d+-\d+)\.(\d+)$")]
    private static partial Regex Receipt();

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Task MigrateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        await Db.PingAsync();
        return true;
    }

    // ── Subscriptions ───────────────────────────────────────────────────────

    public async Task<DateTimeOffset> UpsertSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        var db = Db;
        var expiresAt = DateTimeOffset.UtcNow.Add(_options.SubscriptionIdleTimeout);
        await db.SetAddAsync(TopicsKey, topic);
        try
        {
            // "$": the subscription receives messages published from now on.
            await db.StreamCreateConsumerGroupAsync(StreamKey(topic), subscriberId, StreamPosition.NewMessages, createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP"))
        {
            // Renewal of an existing subscription.
        }
        await db.HashSetAsync(SubsKey(topic), subscriberId, expiresAt.ToUnixTimeMilliseconds());
        return expiresAt;
    }

    public async Task<bool> TouchSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        var db = Db;
        var expiresAt = await db.HashGetAsync(SubsKey(topic), subscriberId);
        if (expiresAt.IsNull) return false;
        if ((long)expiresAt <= NowMs)
        {
            await RemoveSubscriptionAsync(topic, subscriberId, cancellationToken);
            return false;
        }
        await db.HashSetAsync(SubsKey(topic), subscriberId, NowMs + (long)_options.SubscriptionIdleTimeout.TotalMilliseconds);
        return true;
    }

    public async Task<bool> RemoveSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        var db = Db;
        var existed = await db.HashDeleteAsync(SubsKey(topic), subscriberId);
        try { await db.StreamDeleteConsumerGroupAsync(StreamKey(topic), subscriberId); }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP") || ex.Message.Contains("no such key")) { }
        await db.KeyDeleteAsync(ErrorsKey(topic, subscriberId));
        // Dead-letters outlive the subscription for the retention period, as on Postgres.
        await db.KeyExpireAsync(DeadKey(topic, subscriberId), _options.RetainDeadLettered);
        return existed;
    }

    public async Task<SubscriptionStats?> GetStatsAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        var db = Db;
        var expiresAt = await db.HashGetAsync(SubsKey(topic), subscriberId);
        if (expiresAt.IsNull || (long)expiresAt <= NowMs) return null;

        var group = (await db.StreamGroupInfoAsync(StreamKey(topic))).FirstOrDefault(g => g.Name == subscriberId);
        long pending = group.PendingMessageCount + (group.Lag ?? 0);
        return new SubscriptionStats(DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAt), pending,
            await db.StreamLengthAsync(DeadKey(topic, subscriberId)));
    }

    // ── Messages ────────────────────────────────────────────────────────────

    public async Task<int> PublishAsync(string topic, TopicMessage message, CancellationToken cancellationToken)
    {
        var db = Db;
        int active = 0;
        long now = NowMs;
        foreach (var sub in await db.HashGetAllAsync(SubsKey(topic)))
        {
            // An expired subscription's group must not get this copy, so it is removed first.
            if ((long)sub.Value <= now) await RemoveSubscriptionAsync(topic, sub.Name!, cancellationToken);
            else active++;
        }
        if (active == 0) return 0;

        var fields = new List<NameValueEntry>
        {
            new("messageid", message.MessageId.ToString()),
            new("publishedat", now),
            new("payload", message.Payload)
        };
        if (message.TraceContext != null) fields.Add(new("tracecontext", message.TraceContext));
        await db.StreamAddAsync(StreamKey(topic), fields.ToArray());
        return active;
    }

    public async Task<IReadOnlyList<TopicDelivery>> ClaimAsync(string topic, string subscriberId, int maxMessages, CancellationToken cancellationToken)
    {
        var db = Db;
        var stream = StreamKey(topic);
        var claimed = new List<TopicDelivery>();

        // Redeliveries: entries idle for a whole lease (lease expired, or nacked and due).
        StreamAutoClaimResult reclaimed;
        try
        {
            reclaimed = await db.StreamAutoClaimAsync(stream, subscriberId, ConsumerName,
                (long)_options.ClaimLease.TotalMilliseconds, "0-0", maxMessages);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP"))
        {
            return claimed;
        }

        if (reclaimed.ClaimedEntries.Length > 0)
        {
            var counts = await DeliveryCountsAsync(db, stream, subscriberId, reclaimed.ClaimedEntries);
            foreach (var entry in reclaimed.ClaimedEntries)
            {
                // The count includes this delivery: above MaxRetries means MaxRetries went unacknowledged.
                int attempt = counts.TryGetValue(entry.Id.ToString(), out var count) ? count : 2;
                if (attempt > _options.MaxRetries)
                {
                    await DeadLetterAsync(db, topic, subscriberId, entry, attempt - 1);
                    continue;
                }
                claimed.Add(ToDelivery(entry, attempt));
            }
        }

        // New entries.
        if (claimed.Count < maxMessages)
        {
            var fresh = await db.StreamReadGroupAsync(stream, subscriberId, ConsumerName, StreamPosition.NewMessages,
                maxMessages - claimed.Count);
            claimed.AddRange(fresh.Select(e => ToDelivery(e, 1)));
        }
        return claimed;
    }

    private static async Task<Dictionary<string, int>> DeliveryCountsAsync(IDatabase db, string stream, string group, StreamEntry[] entries)
    {
        var ids = entries.Select(e => e.Id.ToString()).ToList();
        var pending = await db.StreamPendingMessagesAsync(stream, group, entries.Length + 100, ConsumerName,
            entries.Min(e => e.Id), entries.Max(e => e.Id));
        return pending.Where(p => ids.Contains(p.MessageId.ToString()))
            .ToDictionary(p => p.MessageId.ToString(), p => p.DeliveryCount);
    }

    private async Task DeadLetterAsync(IDatabase db, string topic, string subscriberId, StreamEntry entry, int attempts)
    {
        var error = await db.HashGetAsync(ErrorsKey(topic, subscriberId), entry.Id);
        var fields = entry.Values.ToList();
        fields.Add(new("attempts", attempts));
        fields.Add(new("failedat", NowMs));
        fields.Add(new("lasterror", error.IsNull ? $"Not acknowledged after {attempts} deliveries." : error.ToString()));

        var tran = db.CreateTransaction();
        _ = tran.StreamAddAsync(DeadKey(topic, subscriberId), fields.ToArray());
        _ = tran.StreamAcknowledgeAsync(StreamKey(topic), subscriberId, entry.Id);
        _ = tran.HashDeleteAsync(ErrorsKey(topic, subscriberId), entry.Id);
        await tran.ExecuteAsync();

        TopicStoreLog.DeadLettered(_logger, topic, subscriberId,
            Guid.TryParse(Field(entry, "messageid"), out var id) ? id : Guid.Empty, attempts);
    }

    public async Task<ReceiptOutcome> AckAsync(string topic, string subscriberId, string receipt, CancellationToken cancellationToken)
    {
        if (ParseReceipt(receipt) is not { } parsed) return ReceiptOutcome.InvalidReceipt;
        var (entryId, deliveries) = parsed;
        var db = Db;
        if (await PendingDeliveriesAsync(db, StreamKey(topic), subscriberId, entryId) != deliveries) return ReceiptOutcome.LeaseLost;

        var acked = await db.StreamAcknowledgeAsync(StreamKey(topic), subscriberId, entryId);
        await db.HashDeleteAsync(ErrorsKey(topic, subscriberId), entryId);
        return acked > 0 ? ReceiptOutcome.Done : ReceiptOutcome.LeaseLost;
    }

    // XCLAIM … IDLE sets how long the entry has been idle, so XAUTOCLAIM (which waits for a whole
    // lease of idleness) takes it again after the retry delay. JUSTID leaves the delivery count alone.
    public async Task<ReceiptOutcome> NackAsync(string topic, string subscriberId, string receipt, string? error, CancellationToken cancellationToken)
    {
        if (ParseReceipt(receipt) is not { } parsed) return ReceiptOutcome.InvalidReceipt;
        var (entryId, deliveries) = parsed;
        var db = Db;
        if (await PendingDeliveriesAsync(db, StreamKey(topic), subscriberId, entryId) != deliveries) return ReceiptOutcome.LeaseLost;

        var lease = (long)_options.ClaimLease.TotalMilliseconds;
        var delay = (long)_options.GetRetryDelay(deliveries).TotalMilliseconds;
        var idle = Math.Max(0, lease - delay);
        var result = await db.ExecuteAsync("XCLAIM", StreamKey(topic), subscriberId, ConsumerName, 0, entryId, "IDLE", idle, "JUSTID");
        if (result.IsNull || ((RedisResult[])result!).Length == 0) return ReceiptOutcome.LeaseLost;

        if (error != null) await db.HashSetAsync(ErrorsKey(topic, subscriberId), entryId, error);
        return ReceiptOutcome.Done;
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var db = Db;
        foreach (var topicValue in await db.SetMembersAsync(TopicsKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var topic = topicValue.ToString();
            var stream = StreamKey(topic);
            long now = NowMs;

            foreach (var sub in await db.HashGetAllAsync(SubsKey(topic)))
            {
                if ((long)sub.Value <= now) await RemoveSubscriptionAsync(topic, sub.Name!, cancellationToken);
                else await db.ExecuteAsync("XTRIM", DeadKey(topic, sub.Name!), "MINID",
                    $"{now - (long)_options.RetainDeadLettered.TotalMilliseconds}-0");
            }

            if (!await db.KeyExistsAsync(stream)) continue;

            // Trim what every subscription has acknowledged: up to the oldest entry any group still
            // has pending or has not read yet. With no subscriptions nothing is needed.
            string? minId = null;
            foreach (var group in await db.StreamGroupInfoAsync(stream))
            {
                var summary = await db.StreamPendingAsync(stream, group.Name);
                if (summary.PendingMessageCount > 0) minId = MinId(minId, summary.LowestPendingMessageId.ToString());

                var nothingRead = string.IsNullOrEmpty(group.LastDeliveredId) || group.LastDeliveredId == "0-0";
                var unread = await db.StreamRangeAsync(stream, nothingRead ? "-" : $"({group.LastDeliveredId}", "+", count: 1);
                if (unread.Length > 0) minId = MinId(minId, unread[0].Id.ToString());
            }

            if (minId != null) await db.ExecuteAsync("XTRIM", stream, "MINID", minId);
            else await db.StreamTrimAsync(stream, 0);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (string EntryId, int Deliveries)? ParseReceipt(string receipt)
    {
        var match = Receipt().Match(receipt);
        return match.Success && int.TryParse(match.Groups[2].Value, out var deliveries)
            ? (match.Groups[1].Value, deliveries)
            : null;
    }

    // The entry's delivery count, or 0 when it is no longer pending (acknowledged or dead-lettered).
    private static async Task<int> PendingDeliveriesAsync(IDatabase db, string stream, string group, string entryId)
    {
        try
        {
            var pending = await db.StreamPendingMessagesAsync(stream, group, 1, ConsumerName, entryId, entryId);
            return pending.Length == 0 ? 0 : pending[0].DeliveryCount;
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP"))
        {
            return 0;
        }
    }

    private static TopicDelivery ToDelivery(StreamEntry entry, int attempt) => new(
        Guid.TryParse(Field(entry, "messageid"), out var id) ? id : Guid.Empty,
        $"{entry.Id}.{attempt}",
        long.TryParse(Field(entry, "publishedat"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow,
        attempt,
        Field(entry, "tracecontext"),
        Field(entry, "payload") ?? "null");

    private static string? Field(StreamEntry entry, string name)
    {
        var field = entry.Values.FirstOrDefault(v => v.Name == name);
        return field.Value.IsNull ? null : field.Value.ToString();
    }

    private static string MinId(string? a, string b)
    {
        if (a == null) return b;
        static (long, long) Parse(string id)
        {
            var parts = id.Split('-');
            return (long.Parse(parts[0]), parts.Length > 1 ? long.Parse(parts[1]) : 0);
        }
        return Parse(a).CompareTo(Parse(b)) <= 0 ? a : b;
    }
}
