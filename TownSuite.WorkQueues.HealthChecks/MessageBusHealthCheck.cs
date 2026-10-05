using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TownSuite.WorkQueues.HealthChecks;

/// <summary>Thresholds checked for one message type's queue.</summary>
public sealed class QueueHealthThresholds
{
    /// <summary>
    /// Report <see cref="MessageBusHealthCheckOptions.FailureStatus"/> when the oldest deliverable
    /// message has waited longer than this. Catches stopped workers, slow consumers and channels with
    /// no subscriber. <see langword="null"/> skips the check.
    /// </summary>
    public TimeSpan? MaxOldestReadyAge { get; set; }

    /// <summary>Report a failure when more than this many messages are pending. <see langword="null"/> skips it.</summary>
    public long? MaxPending { get; set; }

    /// <summary>Report a failure when more than this many messages are dead-lettered. <see langword="null"/> skips it.</summary>
    public long? MaxDeadLettered { get; set; }

    /// <summary>
    /// Report a failure when more than this many faults are waiting to be delivered — a fault consumer
    /// keeps failing or none is subscribed. <see langword="null"/> skips it.
    /// </summary>
    public long? MaxPendingFaults { get; set; }
}

/// <summary>Configures <see cref="MessageBusHealthCheck"/>.</summary>
public sealed class MessageBusHealthCheckOptions
{
    internal List<(string Channel, Func<IMessageBus, CancellationToken, Task<QueueStatistics>> Read, QueueHealthThresholds Thresholds)> Queues { get; } = new();

    /// <summary>Status reported when a queue threshold is exceeded. Defaults to <see cref="HealthStatus.Degraded"/>.</summary>
    public HealthStatus FailureStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>Checks the queue of message type <typeparamref name="T"/> against <paramref name="configure"/>'s thresholds.</summary>
    public MessageBusHealthCheckOptions Queue<T>(Action<QueueHealthThresholds> configure)
    {
        var thresholds = new QueueHealthThresholds();
        configure(thresholds);
        Queues.Add((typeof(T).FullName ?? typeof(T).Name, (bus, ct) => bus.GetQueueStatistics<T>(ct), thresholds));
        return this;
    }
}

/// <summary>
/// Reports <see cref="HealthStatus.Unhealthy"/> when the bus's polling loops have stopped, and the
/// configured failure status when a queue threshold is exceeded. Queue statistics are included in
/// the result data.
/// </summary>
public sealed class MessageBusHealthCheck(IMessageBus bus, MessageBusHealthCheckOptions options) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!bus.IsPolling)
            return HealthCheckResult.Unhealthy("Message bus polling has stopped — no messages will be processed.");

        var data = new Dictionary<string, object>();
        var problems = new List<string>();

        foreach (var (channel, read, t) in options.Queues)
        {
            QueueStatistics stats;
            try { stats = await read(bus, cancellationToken); }
            catch (Exception ex)
            {
                return new HealthCheckResult(context.Registration.FailureStatus,
                    $"Could not read queue statistics for {channel}.", ex, data);
            }

            data[$"{channel}.pending"]       = stats.PendingCount;
            data[$"{channel}.deadlettered"]  = stats.DeadLetteredCount;
            data[$"{channel}.pendingFaults"] = stats.PendingFaultCount;
            if (stats.OldestReadyAge is { } age) data[$"{channel}.oldestReadyAgeSeconds"] = age.TotalSeconds;

            if (t.MaxOldestReadyAge is { } maxAge && stats.OldestReadyAge > maxAge)
                problems.Add($"{channel}: oldest message has waited {stats.OldestReadyAge.Value.TotalSeconds:N0}s (limit {maxAge.TotalSeconds:N0}s)");
            if (t.MaxPending is { } maxPending && stats.PendingCount > maxPending)
                problems.Add($"{channel}: {stats.PendingCount} pending (limit {maxPending})");
            if (t.MaxDeadLettered is { } maxDead && stats.DeadLetteredCount > maxDead)
                problems.Add($"{channel}: {stats.DeadLetteredCount} dead-lettered (limit {maxDead})");
            if (t.MaxPendingFaults is { } maxFaults && stats.PendingFaultCount > maxFaults)
                problems.Add($"{channel}: {stats.PendingFaultCount} undelivered faults (limit {maxFaults})");
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy("Message bus is polling.", data)
            : new HealthCheckResult(options.FailureStatus, string.Join("; ", problems), data: data);
    }
}

/// <summary>Registration helpers for <see cref="MessageBusHealthCheck"/>.</summary>
public static class MessageBusHealthCheckExtensions
{
    /// <summary>
    /// Adds a health check for the <see cref="IMessageBus"/> registered in DI, or the one returned by
    /// <paramref name="busFactory"/> (use it when an app runs several buses, e.g. one per tenant).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks().AddMessageBus(configure: o => o
    ///     .Queue&lt;CartAddItemRequested&gt;(q => { q.MaxOldestReadyAge = TimeSpan.FromSeconds(30); q.MaxPendingFaults = 0; })
    ///     .Queue&lt;PaymentConfirmed&gt;(q => q.MaxDeadLettered = 0));
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddMessageBus(this IHealthChecksBuilder builder,
        string name = "message-bus",
        Action<MessageBusHealthCheckOptions>? configure = null,
        Func<IServiceProvider, IMessageBus>? busFactory = null,
        IEnumerable<string>? tags = null)
    {
        var options = new MessageBusHealthCheckOptions();
        configure?.Invoke(options);

        return builder.Add(new HealthCheckRegistration(name,
            sp => new MessageBusHealthCheck(busFactory?.Invoke(sp) ?? sp.GetRequiredService<IMessageBus>(), options),
            HealthStatus.Unhealthy, tags));
    }
}
