using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TownSuite.WorkQueues;

/// <summary>
/// <see cref="System.Diagnostics.Metrics"/> instruments emitted by every message bus transport.
/// Subscribe with OpenTelemetry via <c>.AddMeter(WorkQueueMetrics.MeterName)</c>.
/// Every measurement is tagged with <c>messaging.destination.name</c> (the channel) and
/// <c>messaging.system</c> (the transport: postgres, sqlserver, redis or sqlite).
/// </summary>
/// <remarks>
/// Queue depth, dead-letter counts and the age of the oldest waiting message need a query, so
/// they are published as gauges only for queues registered with <see cref="TrackQueue{T}"/>.
/// </remarks>
public static class WorkQueueMetrics
{
    /// <summary>The meter name to subscribe to.</summary>
    public const string MeterName = "TownSuite.WorkQueues";

    private static readonly Meter Meter = new(MeterName,
        typeof(WorkQueueMetrics).Assembly.GetName().Version?.ToString());

    private static readonly Counter<long> Published = Meter.CreateCounter<long>(
        "townsuite.workqueues.messages.published", "{message}", "Messages published.");

    private static readonly Counter<long> Processed = Meter.CreateCounter<long>(
        "townsuite.workqueues.messages.processed", "{message}", "Messages delivered successfully.");

    private static readonly Counter<long> Retried = Meter.CreateCounter<long>(
        "townsuite.workqueues.messages.retried", "{message}", "Failed deliveries that will be retried.");

    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "townsuite.workqueues.messages.deadlettered", "{message}", "Messages moved to the dead-letter state.");

    private static readonly Counter<long> Expired = Meter.CreateCounter<long>(
        "townsuite.workqueues.messages.expired", "{message}", "Messages dead-lettered because they expired before delivery.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "townsuite.workqueues.message.duration", "s", "Time spent in consumers per delivery attempt.");

    /// <summary>Records a published message.</summary>
    public static void RecordPublished(string transport, string channel) =>
        Published.Add(1, Tags(transport, channel));

    /// <summary>Records a successful delivery and how long the consumers took.</summary>
    public static void RecordProcessed(string transport, string channel, TimeSpan elapsed)
    {
        var tags = Tags(transport, channel);
        Processed.Add(1, tags);
        Duration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>Records a failed delivery that will be retried.</summary>
    public static void RecordRetried(string transport, string channel, TimeSpan elapsed)
    {
        var tags = Tags(transport, channel);
        Retried.Add(1, tags);
        Duration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>Records a message moved to the dead-letter state.</summary>
    /// <param name="transport">The transport name.</param>
    /// <param name="channel">The channel name.</param>
    /// <param name="elapsed">Consumer time for the final attempt; <see langword="null"/> when the
    /// message was dead-lettered without a delivery attempt (Redis reclaim).</param>
    public static void RecordDeadLettered(string transport, string channel, TimeSpan? elapsed)
    {
        var tags = Tags(transport, channel);
        DeadLettered.Add(1, tags);
        if (elapsed is { } e)
            Duration.Record(e.TotalSeconds, tags);
    }

    /// <summary>Records a message that expired before it was delivered.</summary>
    public static void RecordExpired(string transport, string channel) =>
        Expired.Add(1, Tags(transport, channel));

    // ── Queue gauges ────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<QueueTracker, byte> Tracked = new();
    private static int _gaugesCreated;

    /// <summary>
    /// Publishes gauges for the queue of message type <typeparamref name="T"/> on <paramref name="bus"/>:
    /// <c>townsuite.workqueues.queue.pending</c>, <c>.deadlettered</c>, <c>.pending_faults</c> and
    /// <c>.oldest_ready_age</c> (seconds). Statistics are read in the background every
    /// <paramref name="refreshInterval"/> (default 30 seconds), so collection never queries the store.
    /// Dispose the result to stop tracking.
    /// </summary>
    public static IAsyncDisposable TrackQueue<T>(IMessageBus bus, TimeSpan? refreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        EnsureGauges();

        var transport = bus is MessageBusBase b ? b.Transport : bus.GetType().Name;
        var tracker = new QueueTracker(transport, refreshInterval ?? TimeSpan.FromSeconds(30),
            ct => bus.GetQueueStatistics<T>(ct));
        Tracked.TryAdd(tracker, 0);
        tracker.Start(() => Tracked.TryRemove(tracker, out _));
        return tracker;
    }

    private static void EnsureGauges()
    {
        if (Interlocked.Exchange(ref _gaugesCreated, 1) == 1) return;

        Meter.CreateObservableGauge("townsuite.workqueues.queue.pending",
            () => Observe(s => s.PendingCount), "{message}", "Messages not yet processed or dead-lettered.");
        Meter.CreateObservableGauge("townsuite.workqueues.queue.deadlettered",
            () => Observe(s => s.DeadLetteredCount), "{message}", "Dead-lettered messages held for replay.");
        Meter.CreateObservableGauge("townsuite.workqueues.queue.pending_faults",
            () => Observe(s => s.PendingFaultCount), "{message}", "Dead-letters whose Fault<T> has not been delivered.");
        Meter.CreateObservableGauge("townsuite.workqueues.queue.oldest_ready_age",
            () => Observe(s => s.OldestReadyAge?.TotalSeconds ?? 0), "s", "How long the oldest deliverable message has waited.");
    }

    private static IEnumerable<Measurement<T>> Observe<T>(Func<QueueStatistics, T> select) where T : struct =>
        Tracked.Keys
            .Where(t => t.Latest != null)
            .Select(t => new Measurement<T>(select(t.Latest!), Tags(t.Transport, t.Latest!.Channel)))
            .ToArray();

    private sealed class QueueTracker(string transport, TimeSpan interval, Func<CancellationToken, Task<QueueStatistics>> read)
        : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;

        public string Transport { get; } = transport;
        public QueueStatistics? Latest { get; private set; }

        public void Start(Action onStopped)
        {
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        // A failed read keeps the previous snapshot; the next refresh tries again.
                        try { Latest = await read(_cts.Token); }
                        catch (Exception) when (!_cts.IsCancellationRequested) { }
                        await Task.Delay(interval, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally { onStopped(); }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _loop;
            _cts.Dispose();
        }
    }

    private static TagList Tags(string transport, string channel) => new()
    {
        { "messaging.system", transport },
        { "messaging.destination.name", channel }
    };
}
