namespace TownSuite.WorkQueues;

/// <summary>Per-message options for <see cref="IMessageBus"/> publish calls.</summary>
public sealed class PublishOptions
{
    /// <summary>Earliest time the message may be delivered. <see langword="null"/> delivers immediately.</summary>
    public DateTimeOffset? DeliverAfter { get; init; }

    /// <summary>
    /// Time after which the message must not be delivered. A message still undelivered at this
    /// time is dead-lettered without calling consumers, and its <see cref="Fault{T}"/> has
    /// <see cref="Fault{T}.Expired"/> set. <see langword="null"/> (the default) never expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Shorthand for an <see cref="ExpiresAt"/> relative to now.</summary>
    public TimeSpan? TimeToLive
    {
        init => ExpiresAt = value.HasValue ? DateTimeOffset.UtcNow.Add(value.Value) : null;
    }

    internal static readonly PublishOptions None = new();
}

/// <summary>
/// The exception recorded when a message is dead-lettered because its
/// <see cref="PublishOptions.ExpiresAt"/> passed before it was delivered.
/// </summary>
public sealed class MessageExpiredException : Exception
{
    /// <summary>Creates the exception for a message that expired at <paramref name="expiresAt"/>.</summary>
    public MessageExpiredException(DateTimeOffset expiresAt)
        : base($"Message expired at {expiresAt:O} before it was delivered.") { }
}
