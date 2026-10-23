using Microsoft.Extensions.Logging;

namespace TownSuite.WorkQueues.Server.Stores;

// Logging and metrics shared by the stores. Topic copies are reported on the WorkQueueMetrics meter
// with messaging.system "http" and the topic as the destination.
internal static class TopicStoreLog
{
    public const string Transport = "http";

    public static void DeadLettered(ILogger logger, string topic, string subscriberId, Guid messageId, int attempts)
    {
        logger.LogWarning("Message {MessageId} on topic {Topic} dead-lettered for subscription {SubscriberId} after {Attempts} unacknowledged deliveries",
            messageId, topic, subscriberId, attempts);
        WorkQueueMetrics.RecordDeadLettered(Transport, topic, null);
    }
}
