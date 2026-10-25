namespace TownSuite.WorkQueues.Server.Stores;

/// <summary>
/// Topic storage behind the HTTP API: subscriptions, one copy of each message per subscription,
/// and per-copy claim, acknowledgement, retry and dead-lettering.
/// </summary>
/// <remarks>
/// Semantics every implementation keeps:
/// <list type="bullet">
/// <item>A publish reaches only subscriptions active at that moment (not expired).</item>
/// <item>A claim hands out a copy for <see cref="TopicServerOptions.ClaimLease"/> and counts an attempt.
/// An unacknowledged copy is claimable again when its lease ends, or after the retry delay when it
/// was negatively acknowledged.</item>
/// <item>A copy delivered <see cref="TopicServerOptions.MaxRetries"/> times without acknowledgement is
/// dead-lettered instead of being delivered again.</item>
/// <item>Receivers sharing a subscription share its copies.</item>
/// </list>
/// </remarks>
public interface ITopicStore
{
    /// <summary>Creates or upgrades the store's schema. Idempotent.</summary>
    Task MigrateAsync(CancellationToken cancellationToken);

    /// <summary>Registers or renews a subscription; returns when it now expires.</summary>
    Task<DateTimeOffset> UpsertSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken);

    /// <summary>
    /// Renews an existing, unexpired subscription. Returns <see langword="false"/> when there is none,
    /// in which case an expired one is removed.
    /// </summary>
    Task<bool> TouchSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken);

    /// <summary>Removes a subscription and its waiting copies. Returns whether it existed.</summary>
    Task<bool> RemoveSubscriptionAsync(string topic, string subscriberId, CancellationToken cancellationToken);

    /// <summary>Stores one copy for each active subscription on the topic; returns how many.</summary>
    Task<int> PublishAsync(string topic, TopicMessage message, CancellationToken cancellationToken);

    /// <summary>Claims up to <paramref name="maxMessages"/> due copies for the subscription.</summary>
    Task<IReadOnlyList<TopicDelivery>> ClaimAsync(string topic, string subscriberId, int maxMessages, CancellationToken cancellationToken);

    /// <summary>Acknowledges a claimed copy.</summary>
    Task<ReceiptOutcome> AckAsync(string topic, string subscriberId, string receipt, CancellationToken cancellationToken);

    /// <summary>Releases a claimed copy for retry after the retry delay, recording <paramref name="error"/>.</summary>
    Task<ReceiptOutcome> NackAsync(string topic, string subscriberId, string receipt, string? error, CancellationToken cancellationToken);

    /// <summary>Waiting and dead-lettered copies for an active subscription, or <see langword="null"/> when there is none.</summary>
    Task<SubscriptionStats?> GetStatsAsync(string topic, string subscriberId, CancellationToken cancellationToken);

    /// <summary>Removes expired subscriptions, their copies, and old acknowledged and dead-lettered copies.</summary>
    Task CleanupAsync(CancellationToken cancellationToken);

    /// <summary>Whether the backend can be reached.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken);
}

/// <summary>A message to publish.</summary>
/// <param name="MessageId">The id every copy carries.</param>
/// <param name="Payload">The JSON body.</param>
/// <param name="TraceContext">The publisher's W3C trace context (<c>traceparent</c>, optionally a space and <c>tracestate</c>).</param>
public sealed record TopicMessage(Guid MessageId, string Payload, string? TraceContext);

/// <summary>A claimed copy.</summary>
/// <param name="MessageId">The publish id.</param>
/// <param name="Receipt">Opaque handle for ack/nack; only valid while this claim's lease holds.</param>
/// <param name="PublishedAt">Publish time.</param>
/// <param name="Attempt">This delivery's attempt number, starting at 1.</param>
/// <param name="TraceContext">The stored trace context, if any.</param>
/// <param name="Payload">The JSON body.</param>
public sealed record TopicDelivery(Guid MessageId, string Receipt, DateTimeOffset PublishedAt, int Attempt,
    string? TraceContext, string Payload);

/// <summary>A subscription's backlog.</summary>
/// <param name="ExpiresAt">When the subscription expires unless renewed or polled.</param>
/// <param name="Pending">Copies not yet acknowledged or dead-lettered (including ones claimed now).</param>
/// <param name="DeadLettered">Copies dead-lettered and still retained.</param>
public sealed record SubscriptionStats(DateTimeOffset ExpiresAt, long Pending, long DeadLettered);

/// <summary>Result of an ack or nack.</summary>
public enum ReceiptOutcome
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>The claim no longer holds the copy (its lease expired, or it was already acknowledged).</summary>
    LeaseLost,

    /// <summary>The receipt is not one this store issued.</summary>
    InvalidReceipt
}
