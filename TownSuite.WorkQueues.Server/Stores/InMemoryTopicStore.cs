using Microsoft.Extensions.Logging;

namespace TownSuite.WorkQueues.Server.Stores;

/// <summary>
/// Keeps topics in process memory. Lost on restart and not shared between replicas: for local
/// development and tests only (<c>Backend = memory</c>).
/// </summary>
internal sealed class InMemoryTopicStore(TopicServerOptions options, ILogger<InMemoryTopicStore> logger) : ITopicStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Topic, string SubscriberId), Subscription> _subscriptions = new();
    private long _sequence;

    private sealed class Subscription
    {
        public DateTime ExpiresAt;
        public readonly List<Copy> Pending = new();
        public readonly List<(Copy Copy, DateTime FailedAt)> DeadLettered = new();
    }

    private sealed class Copy
    {
        public long Sequence;
        public required TopicMessage Message;
        public DateTime PublishedAt;
        public int Attempts;
        public DateTime AvailableAt;
        public Guid? Token;
        public string? LastError;
    }

    public Task MigrateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<DateTimeOffset> UpsertSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue((topic, subscriberId), out var sub))
                _subscriptions[(topic, subscriberId)] = sub = new Subscription();
            sub.ExpiresAt = DateTime.UtcNow.Add(options.SubscriptionIdleTimeout);
            return Task.FromResult(new DateTimeOffset(sub.ExpiresAt, TimeSpan.Zero));
        }
    }

    public Task<bool> TouchSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue((topic, subscriberId), out var sub)) return Task.FromResult(false);
            if (sub.ExpiresAt <= DateTime.UtcNow)
            {
                _subscriptions.Remove((topic, subscriberId));
                return Task.FromResult(false);
            }
            sub.ExpiresAt = DateTime.UtcNow.Add(options.SubscriptionIdleTimeout);
            return Task.FromResult(true);
        }
    }

    public Task<bool> RemoveSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_subscriptions.Remove((topic, subscriberId)));
    }

    public Task<int> PublishAsync(string topic, TopicMessage message, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            int copies = 0;
            foreach (var ((t, _), sub) in _subscriptions)
            {
                if (t != topic || sub.ExpiresAt <= now) continue;
                sub.Pending.Add(new Copy { Sequence = ++_sequence, Message = message, PublishedAt = now, AvailableAt = now });
                copies++;
            }
            return Task.FromResult(copies);
        }
    }

    public Task<IReadOnlyList<TopicDelivery>> ClaimAsync(string topic, string subscriberId, int maxMessages, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var claimed = new List<TopicDelivery>();
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue((topic, subscriberId), out var sub))
                return Task.FromResult<IReadOnlyList<TopicDelivery>>(claimed);

            foreach (var copy in sub.Pending.Where(c => c.AvailableAt <= now && c.Attempts >= options.MaxRetries).ToList())
            {
                sub.Pending.Remove(copy);
                sub.DeadLettered.Add((copy, now));
                TopicStoreLog.DeadLettered(logger, topic, subscriberId, copy.Message.MessageId, copy.Attempts);
            }

            foreach (var copy in sub.Pending.Where(c => c.AvailableAt <= now).OrderBy(c => c.Sequence).Take(maxMessages))
            {
                copy.Attempts++;
                copy.AvailableAt = now.Add(options.ClaimLease);
                copy.Token = Guid.NewGuid();
                claimed.Add(new TopicDelivery(copy.Message.MessageId, $"{copy.Sequence}.{copy.Token:N}",
                    new DateTimeOffset(copy.PublishedAt, TimeSpan.Zero), copy.Attempts, copy.Message.TraceContext, copy.Message.Payload));
            }
        }
        return Task.FromResult<IReadOnlyList<TopicDelivery>>(claimed);
    }

    public Task<ReceiptOutcome> AckAsync(string topic, string subscriberId, string receipt, CancellationToken cancellationToken) =>
        Task.FromResult(WithClaim(topic, subscriberId, receipt, (sub, copy) => sub.Pending.Remove(copy)));

    public Task<ReceiptOutcome> NackAsync(string topic, string subscriberId, string receipt, string? error, CancellationToken cancellationToken) =>
        Task.FromResult(WithClaim(topic, subscriberId, receipt, (_, copy) =>
        {
            copy.Token = null;
            copy.AvailableAt = DateTime.UtcNow.Add(options.GetRetryDelay(copy.Attempts));
            copy.LastError = error;
        }));

    private ReceiptOutcome WithClaim(string topic, string subscriberId, string receipt, Action<Subscription, Copy> action)
    {
        var parts = receipt.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var sequence) || !Guid.TryParseExact(parts[1], "N", out var token))
            return ReceiptOutcome.InvalidReceipt;

        lock (_lock)
        {
            if (!_subscriptions.TryGetValue((topic, subscriberId), out var sub)) return ReceiptOutcome.LeaseLost;
            var copy = sub.Pending.FirstOrDefault(c => c.Sequence == sequence);
            // A claim whose lease ran out still holds the copy until someone else claims it.
            if (copy == null || copy.Token != token) return ReceiptOutcome.LeaseLost;
            action(sub, copy);
            return ReceiptOutcome.Done;
        }
    }

    public Task CleanupAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var key in _subscriptions.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
                _subscriptions.Remove(key);
            foreach (var sub in _subscriptions.Values)
                sub.DeadLettered.RemoveAll(d => d.FailedAt < now - options.RetainDeadLettered);
        }
        return Task.CompletedTask;
    }

    public Task<SubscriptionStats?> GetStatsAsync(string topic, string subscriberId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue((topic, subscriberId), out var sub) || sub.ExpiresAt <= DateTime.UtcNow)
                return Task.FromResult<SubscriptionStats?>(null);
            return Task.FromResult<SubscriptionStats?>(new SubscriptionStats(
                new DateTimeOffset(sub.ExpiresAt, TimeSpan.Zero), sub.Pending.Count, sub.DeadLettered.Count));
        }
    }
}
