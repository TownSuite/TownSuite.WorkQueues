using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TownSuite.WorkQueues.Redis;

/// <summary>DI registration helpers for the Redis backend.</summary>
public static class RedisServiceExtensions
{
    /// <summary>
    /// Registers <see cref="IConnectionMultiplexer"/> from a Redis connection string.
    /// Skip this if you are already registering <see cref="IConnectionMultiplexer"/> elsewhere.
    /// </summary>
    public static IServiceCollection AddRedisConnection(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(connectionString));
        return services;
    }

    /// <summary>
    /// Registers <see cref="RedisMessageBus"/> as <see cref="IMessageBus"/> singleton.
    /// Requires <see cref="IConnectionMultiplexer"/> to already be registered.
    /// </summary>
    /// <param name="configureOptions">Sets queue options (key prefix, consumer group, retries, etc.).</param>
    /// <param name="subscribe">Optional. Called with the resolved <see cref="IServiceProvider"/> and
    /// the newly constructed bus so you can call <see cref="IMessageBus.Subscribe{T}"/> at
    /// registration time — matching the pattern used by
    /// <c>AddPostgresMessageBus</c> and <c>AddSqlServerMessageBus</c>.</param>
    public static IServiceCollection AddRedisMessageBus(
        this IServiceCollection services,
        Action<RedisOptions> configureOptions,
        Action<IServiceProvider, RedisMessageBus>? subscribe = null)
    {
        var opts = new RedisOptions();
        configureOptions(opts);

        services.AddSingleton<IMessageBus>(sp =>
        {
            var redis  = sp.GetRequiredService<IConnectionMultiplexer>();
            var logger = sp.GetRequiredService<ILogger<RedisMessageBus>>();
            var bus    = new RedisMessageBus(redis, opts, logger, sp);
            subscribe?.Invoke(sp, bus);
            return bus;
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="RedisWorkQueue"/> as <see cref="IRedisWorkQueue"/> singleton.
    /// Requires <see cref="IConnectionMultiplexer"/> to already be registered.
    /// </summary>
    [Obsolete("Legacy Redis list queue without retry or dead-lettering. Use RedisMessageBus (IMessageBus). See MIGRATING.md.", DiagnosticId = "TSWQ001")]
    public static IServiceCollection AddRedisWorkQueue(
        this IServiceCollection services,
        Action<RedisOptions> configure)
    {
        var opts = new RedisOptions();
        configure(opts);

        services.AddSingleton<IRedisWorkQueue>(sp =>
        {
            var redis = sp.GetRequiredService<IConnectionMultiplexer>();
            return new RedisWorkQueue(redis, opts);
        });

        return services;
    }
}
