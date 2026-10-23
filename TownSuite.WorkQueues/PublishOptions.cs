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

    /// <summary>
    /// Delivery priority within the message's channel. Lower values are delivered first; messages
    /// of equal priority are delivered oldest first. Defaults to <c>0</c>, and may be negative.
    /// Supported by the PostgreSQL, SQL Server and SQLite transports; the Redis transport throws
    /// <see cref="NotSupportedException"/> for a non-zero priority.
    /// </summary>
    public int Priority { get; init; }

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

/// <summary>
/// Thrown by publish when a message serialises to more than <see cref="BatchOptions.MaxMessageSize"/>
/// bytes, and recorded when such a message is found in the store and dead-lettered.
/// </summary>
public sealed class MessageTooLargeException : Exception
{
    /// <summary>Creates the exception for a message of <paramref name="size"/> bytes on <paramref name="channel"/>.</summary>
    public MessageTooLargeException(string channel, long size, long limit)
        : base($"Message on channel '{channel}' is {size} bytes, over the {limit}-byte limit (BatchOptions.MaxMessageSize).")
    {
        Channel = channel;
        Size    = size;
        Limit   = limit;
    }

    /// <summary>The message's channel (its type's full name).</summary>
    public string Channel { get; }

    /// <summary>The serialised UTF-8 size of the message, in bytes.</summary>
    public long Size { get; }

    /// <summary>The configured <see cref="BatchOptions.MaxMessageSize"/>.</summary>
    public long Limit { get; }
}
