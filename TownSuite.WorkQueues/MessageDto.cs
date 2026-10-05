namespace TownSuite.WorkQueues
{
    /// <summary>
    /// A row from the <c>workqueue</c> table, used internally by the message bus polling loop
    /// to carry a message from the database to its in-memory consumers.
    /// </summary>
    public class MessageDto
    {
        /// <summary>Auto-incremented row identifier.</summary>
        public int Id { get; set; }

        /// <summary>Stable unique identifier assigned at publish time.</summary>
        public Guid MessageId { get; set; }

        /// <summary>UTC timestamp at which the row was inserted.</summary>
        public DateTime TimeCreatedUtc { get; set; }

        /// <summary>
        /// Logical channel name. For messages published via <see cref="IMessageBus"/>,
        /// this is <c>typeof(T).FullName</c> of the published message type.
        /// </summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>JSON-serialised message payload.</summary>
        public string Payload { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp set when the message was successfully processed.
        /// <see langword="null"/> for unprocessed and dead-lettered messages.
        /// </summary>
        public DateTime? TimeProcessedUtc { get; set; }

        /// <summary>Number of failed delivery attempts so far.</summary>
        public int RetryCount { get; set; }

        /// <summary>UTC time after which the message must not be delivered, if any.</summary>
        public DateTime? ExpiresAtUtc { get; set; }

        // Transport-specific state for the claimed message (for example a Redis stream entry).
        internal object? Handle { get; set; }

        // Key a pending fault for this message is stored under, when it is not the message id.
        internal string? FaultKey { get; set; }
    }
}
