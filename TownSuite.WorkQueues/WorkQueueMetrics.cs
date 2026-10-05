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
/// Queue depth and the age of the oldest waiting message are not pushed as metrics because
/// they require a query; read them with <see cref="IMessageBus.GetQueueStatistics{T}"/>.
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

    private static TagList Tags(string transport, string channel) => new()
    {
        { "messaging.system", transport },
        { "messaging.destination.name", channel }
    };
}
