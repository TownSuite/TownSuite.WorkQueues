using StackExchange.Redis;
using System.Text.Json;

namespace TownSuite.WorkQueues.Redis;

/// <summary>
/// Redis-backed work queue using Redis Lists (LPUSH / RPOP) for FIFO delivery.
/// </summary>
[Obsolete("Legacy Redis list queue without retry or dead-lettering. Use RedisMessageBus (IMessageBus). See MIGRATING.md.", DiagnosticId = "TSWQ001")]
public class RedisWorkQueue : IRedisWorkQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;

    public RedisWorkQueue(IConnectionMultiplexer redis, RedisOptions options)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task EnqueueAsync<T>(string channel, T payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel must not be empty.", nameof(channel));
        if (channel.Length > 500)
            throw new WorkQueuesException("Channel must not exceed 500 characters.");

        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(payload);
        var db = _redis.GetDatabase();
        await db.ListLeftPushAsync(ListKey(channel), json);
    }

    /// <inheritdoc />
    public async Task<T?> DequeueAsync<T>(string channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel must not be empty.", nameof(channel));

        cancellationToken.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        var value = await db.ListRightPopAsync(ListKey(channel));

        if (value.IsNullOrEmpty)
            return default;

        return LegacyJsonDeserializer.Deserialize<T>((string)value!);
    }

    private string ListKey(string channel) => $"{_options.KeyPrefix}:{channel}";
}
