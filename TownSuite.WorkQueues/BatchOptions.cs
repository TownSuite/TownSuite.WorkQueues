namespace TownSuite.WorkQueues
{
    /// <summary>
    /// Shared configuration for the polling behaviour common to all message bus backends
    /// (PostgreSQL, SQL Server, Redis). All three transport options classes inherit from this.
    /// </summary>
    public class BatchOptions
    {
        /// <summary>
        /// The maximum number of messages to process in a single batch.
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;
        /// <summary>
        /// The maximum time to wait for messages before processing the batch.
        /// </summary>
        public TimeSpan MaxWaitTime { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Poll continuously without delay even when no messages are available.
        /// When <see langword="false"/> (the default) the bus waits <see cref="MaxWaitTime"/> between
        /// empty polls, which reduces CPU and database load. Set to <see langword="true"/> only
        /// in tests or latency-critical scenarios.
        /// </summary>
        public bool ContinuousPolling { get; set; } = false;

        /// <summary>Whether to allow empty batches (i.e., processing when no messages are available).</summary>
        [Obsolete("Use ContinuousPolling instead. AllowEmptyBatches will be removed in a future version.")]
        public bool AllowEmptyBatches { get => ContinuousPolling; set => ContinuousPolling = value; }

        /// <summary>
        /// Maximum number of delivery attempts before a message is moved to the dead-letter state.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Minimum delay between retry attempts. Defaults to <see cref="TimeSpan.Zero"/> (retry
        /// immediately on the next polling cycle). For SQL-backed transports a non-zero value sets
        /// the <c>scheduledfor</c> column to <c>NOW() + RetryDelay</c> so the message is withheld
        /// from polling until the delay elapses.
        /// Redis transports use <c>RedisOptions.ReclaimIdleTime</c> to control retry timing instead.
        /// </summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Multiplier applied to <see cref="RetryDelay"/> for each subsequent retry. Defaults to
        /// <c>1.0</c> (a fixed delay). With <c>RetryDelay = 1s</c> and a multiplier of <c>2.0</c>
        /// the delays are 1s, 2s, 4s, 8s… capped at <see cref="MaxRetryDelay"/>.
        /// Ignored by Redis transports.
        /// </summary>
        public double RetryBackoffMultiplier { get; set; } = 1.0;

        /// <summary>
        /// Upper bound for the computed retry delay when <see cref="RetryBackoffMultiplier"/> is
        /// greater than <c>1.0</c>. <see langword="null"/> (the default) caps the delay at one day.
        /// </summary>
        public TimeSpan? MaxRetryDelay { get; set; }

        /// <summary>
        /// Number of polling loops the bus runs in parallel. Each loop claims and processes its own
        /// batch, so at most <c>MaxConcurrency × MaxBatchSize</c> messages are in flight at once.
        /// Defaults to <c>1</c> (messages are processed one at a time, in order).
        /// </summary>
        public int MaxConcurrency { get; set; } = 1;

        /// <summary>
        /// Decides whether a consumer exception should be retried. Return <see langword="false"/>
        /// to dead-letter the message immediately (the <see cref="Fault{T}"/> consumer still runs).
        /// <see langword="null"/> (the default) retries every exception up to <see cref="MaxRetries"/>.
        /// </summary>
        public Func<Exception, bool>? IsRetryable { get; set; }

        /// <summary>
        /// Returns the delay to apply before the given retry attempt.
        /// </summary>
        /// <param name="attempt">The 1-based number of the failed attempt being retried.</param>
        public TimeSpan GetRetryDelay(int attempt)
        {
            if (RetryDelay <= TimeSpan.Zero) return TimeSpan.Zero;

            var multiplier = RetryBackoffMultiplier <= 1.0 ? 1.0 : RetryBackoffMultiplier;
            var ticks = RetryDelay.Ticks * Math.Pow(multiplier, Math.Max(0, attempt - 1));
            var cap = (MaxRetryDelay ?? TimeSpan.FromDays(1)).Ticks;
            return ticks >= cap ? TimeSpan.FromTicks(cap) : TimeSpan.FromTicks((long)ticks);
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="ex"/> should be retried
        /// according to <see cref="IsRetryable"/>.
        /// </summary>
        public bool ShouldRetry(Exception ex) => IsRetryable?.Invoke(ex) ?? true;
    }
}
