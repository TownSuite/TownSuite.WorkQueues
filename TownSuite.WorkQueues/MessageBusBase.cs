using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TownSuite.WorkQueues;

/// <summary>
/// Shared implementation of <see cref="IMessageBus"/> used by every built-in transport:
/// subscriptions, the polling loops, dispatch, the retry / dead-letter decision, message
/// expiry, at-least-once fault delivery and metrics. A transport supplies only the storage
/// operations (claim, complete, fail, fault bookkeeping and queries).
/// </summary>
public abstract class MessageBusBase : IMessageBus
{
    private readonly ConcurrentDictionary<string, (Type Type, ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>> Handlers)> _handlers = new();
    private readonly FaultRegistry _faults = new();
    private readonly IntervalGate _faultGate;
    private readonly BatchOptions _batchOptions;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string _transport;
    private Task[] _pollingTasks = Array.Empty<Task>();
    private int _disposed;

    private protected readonly CancellationTokenSource Cts = new();
    private protected readonly ILogger Logger;

    private protected MessageBusBase(BatchOptions options, ILogger logger, IServiceProvider? serviceProvider, string transport)
    {
        _batchOptions    = options ?? throw new ArgumentNullException(nameof(options));
        Logger           = logger  ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider;
        _transport       = transport;
        _faultGate       = IntervalGate.ForFaultRedelivery(options);
    }

    /// <summary>
    /// Starts the polling loops. Transports call this last in their constructor, once their own
    /// fields are assigned.
    /// </summary>
    private protected void StartPolling()
    {
        // Yield to the caller so Subscribe() calls made immediately after construction
        // are registered before the first poll cycle runs.
        _pollingTasks = Enumerable.Range(0, Math.Max(1, _batchOptions.MaxConcurrency))
            .Select(_ => Task.Run(async () => { await Task.Yield(); await PollAsync(); }))
            .ToArray();
    }

    /// <summary>The transport name used in metric tags (postgres, sqlserver, redis or sqlite).</summary>
    public string Transport => _transport;

    /// <inheritdoc />
    public bool IsPolling => _pollingTasks.Length > 0 && _pollingTasks.All(t => !t.IsCompleted);

    // ── Subscriptions ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Subscribe<T>(IConsumer<T> consumer)
    {
        Handlers<T>().TryAdd(consumer, async (obj, messageId, sentTime) =>
        {
            if (obj is T message)
                await consumer.Consume(new SimpleConsumeContext<T>(message, Cts.Token, messageId, sentTime));
        });
    }

    /// <summary>
    /// Registers a scoped consumer resolved fresh from an <see cref="IServiceScope"/> on every
    /// message dispatch. Requires an <see cref="IServiceProvider"/> to have been passed to the
    /// bus constructor (the transport's DI extension supplies it).
    /// </summary>
    public void Subscribe<TMessage, TConsumer>() where TConsumer : class, IConsumer<TMessage>
    {
        if (_serviceProvider == null)
            throw new InvalidOperationException(
                "Scoped consumer registration requires IServiceProvider. " +
                $"Pass serviceProvider to the {GetType().Name} constructor, or use the transport's DI extension.");

        var serviceProvider = _serviceProvider;
        Handlers<TMessage>().TryAdd(typeof(TConsumer), async (obj, messageId, sentTime) =>
        {
            if (obj is TMessage message)
            {
                await using var scope = serviceProvider.CreateAsyncScope();
                var consumer = scope.ServiceProvider.GetRequiredService<TConsumer>();
                await consumer.Consume(new SimpleConsumeContext<TMessage>(message, Cts.Token, messageId, sentTime));
            }
        });
    }

    /// <inheritdoc />
    public void SubscribeFault<T>(IConsumer<Fault<T>> consumer) => _faults.Add(consumer);

    private ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>> Handlers<T>() =>
        _handlers.GetOrAdd(ChannelName<T>(),
            _ => (typeof(T), new ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>>())).Handlers;

    /// <summary>Channels this bus has message consumers for.</summary>
    private protected string[] SubscribedChannels =>
        _handlers.Where(kv => !kv.Value.Handlers.IsEmpty).Select(kv => kv.Key).ToArray();

    private protected static string ChannelName<T>()
    {
        var channel = typeof(T).FullName
            ?? throw new InvalidOperationException($"Cannot determine channel name for type {typeof(T)}");

        if (channel.Length > 500)
            throw new ArgumentException($"Message type name exceeds 500 characters: {channel}");

        return channel;
    }

    // ── Publishing ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task Publish<T>(T message, CancellationToken cancellationToken = default) =>
        await PublishAsync(message, PublishOptions.None, cancellationToken);

    /// <inheritdoc />
    public virtual async Task Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default) =>
        await PublishAsync(message, new PublishOptions { DeliverAfter = deliverAfter }, cancellationToken);

    /// <inheritdoc />
    public Task<Guid> Publish<T>(T message, PublishOptions options, CancellationToken cancellationToken = default) =>
        PublishAsync(message, options ?? PublishOptions.None, cancellationToken);

    /// <inheritdoc />
    public virtual Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        PublishOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support transactional publish.");

    private async Task<Guid> PublishAsync<T>(T message, PublishOptions options, CancellationToken cancellationToken)
    {
        var channel = ChannelName<T>();
        var messageId = Guid.NewGuid();
        await InsertAsync(channel, JsonSerializer.Serialize(message), messageId, options, cancellationToken);
        RecordPublished(channel);
        return messageId;
    }

    private protected void RecordPublished(string channel) => WorkQueueMetrics.RecordPublished(_transport, channel);

    /// <summary>Inserts a serialised message.</summary>
    private protected abstract Task InsertAsync(string channel, string payload, Guid messageId,
        PublishOptions options, CancellationToken cancellationToken);

    // ── Queries and maintenance (transport-specific) ────────────────────────

    /// <inheritdoc />
    public abstract Task<int> ReplayDeadLettered<T>(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<bool> ReplayDeadLettered<T>(Guid messageId, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<QueueStatistics> GetQueueStatistics<T>(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<DeadLetteredMessage<T>>> GetDeadLettered<T>(int skip = 0, int take = 100,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<long> PurgeProcessed(DateTimeOffset processedBefore, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<long> PurgeDeadLettered<T>(DateTimeOffset failedBefore, CancellationToken cancellationToken = default);

    // ── Polling ─────────────────────────────────────────────────────────────

    private async Task PollAsync()
    {
        while (!Cts.Token.IsCancellationRequested)
        {
            try
            {
                int processed = await ProcessBatchAsync();
                if (_faultGate.TryEnter())
                    processed += await RedeliverFaultsAsync();

                if (processed == 0 && !_batchOptions.ContinuousPolling)
                    await Task.Delay(_batchOptions.MaxWaitTime, Cts.Token);
            }
            catch (OperationCanceledException) when (Cts.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in {Bus} polling loop", GetType().Name);
                try { await Task.Delay(1000, Cts.Token); } catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>
    /// Claims up to <paramref name="maxMessages"/> deliverable messages on <paramref name="channels"/>.
    /// </summary>
    private protected abstract Task<ClaimedBatch> ClaimAsync(string[] channels, int maxMessages, CancellationToken cancellationToken);

    private async Task<int> ProcessBatchAsync()
    {
        var channels = SubscribedChannels;
        if (channels.Length == 0) return 0;

        await using var batch = await ClaimAsync(channels, _batchOptions.MaxBatchSize, Cts.Token);

        // Faults are delivered only after the batch's outcomes are durable, so a failed commit
        // never reports a dead-letter that did not happen.
        var faults = new List<FaultInfo>(batch.DeadLetteredDuringClaim);

        foreach (var msg in batch.Messages)
        {
            Exception? error = null;
            var stopwatch = Stopwatch.StartNew();

            if (msg.ExpiresAtUtc is { } expiresAt && expiresAt <= DateTime.UtcNow)
            {
                error = new MessageExpiredException(new DateTimeOffset(expiresAt, TimeSpan.Zero));
            }
            else
            {
                try
                {
                    await DispatchAsync(msg);
                }
                catch (Exception ex)
                {
                    error = ex;
                    Logger.LogError(ex, "Handler failed for message {MessageId} on channel {Channel}", msg.MessageId, msg.Channel);
                }
            }
            stopwatch.Stop();

            if (error == null)
            {
                await batch.CompleteAsync(msg);
                WorkQueueMetrics.RecordProcessed(_transport, msg.Channel, stopwatch.Elapsed);
                continue;
            }

            int attempt = msg.RetryCount + 1;
            bool expired = error is MessageExpiredException;
            bool nonRetryable = !expired && !_batchOptions.ShouldRetry(error);
            bool deadLetter = expired || nonRetryable || attempt >= _batchOptions.MaxRetries;
            var stored = StoredError.From(error, nonRetryable);

            // A dead-lettered message reuses its scheduled time as the time its fault may be
            // redelivered, in case the fault consumer below does not run or does not finish.
            var holdFor = deadLetter ? _batchOptions.FaultRedeliveryDelay : _batchOptions.GetRetryDelay(attempt);
            DateTime? scheduledFor = holdFor > TimeSpan.Zero ? DateTime.UtcNow.Add(holdFor) : null;

            await batch.FailAsync(msg, new FailureOutcome(attempt, deadLetter, scheduledFor, stored));

            if (deadLetter)
            {
                WorkQueueMetrics.RecordDeadLettered(_transport, msg.Channel, expired ? null : stopwatch.Elapsed);
                if (expired) WorkQueueMetrics.RecordExpired(_transport, msg.Channel);
                faults.Add(new FaultInfo(msg.Channel, msg.Payload, msg.MessageId, attempt, DateTimeOffset.UtcNow,
                    stored, IsRedelivery: false) { FaultKey = msg.FaultKey });
            }
            else
            {
                WorkQueueMetrics.RecordRetried(_transport, msg.Channel, stopwatch.Elapsed);
            }
        }

        await batch.CommitAsync();

        foreach (var fault in faults)
            await DeliverFaultAsync(fault);

        return batch.Messages.Count + batch.DeadLetteredDuringClaim.Count;
    }

    private async Task DispatchAsync(MessageDto msg)
    {
        if (!_handlers.TryGetValue(msg.Channel, out var entry) || entry.Handlers.IsEmpty)
        {
            Logger.LogWarning("No handler registered for channel {Channel} — message {MessageId} will be skipped",
                msg.Channel, msg.MessageId);
            return;
        }

        var message = LegacyJsonDeserializer.Deserialize(msg.Payload, entry.Type);
        var sentTime = new DateTimeOffset(DateTime.SpecifyKind(msg.TimeCreatedUtc, DateTimeKind.Utc));
        await Task.WhenAll(entry.Handlers.Values.Select(h => h(message!, msg.MessageId, sentTime)));
    }

    // ── Faults ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Claims dead-letters whose fault was never delivered and is due, on <paramref name="channels"/>,
    /// pushing each one's next redelivery time forward by <paramref name="lease"/>.
    /// </summary>
    private protected abstract Task<IReadOnlyList<FaultInfo>> ClaimDueFaultsAsync(string[] channels, int maxFaults,
        TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Records that the fault for a dead-letter was delivered.</summary>
    private protected abstract Task MarkFaultDeliveredAsync(FaultInfo fault);

    private async Task<int> RedeliverFaultsAsync()
    {
        var channels = _faults.Channels;
        if (channels.Length == 0) return 0;

        var due = await ClaimDueFaultsAsync(channels, _batchOptions.MaxBatchSize, _batchOptions.FaultRedeliveryDelay, Cts.Token);
        foreach (var fault in due)
            await DeliverFaultAsync(fault);
        return due.Count;
    }

    // Delivers a fault and records it as delivered. A fault consumer that throws leaves it
    // pending; it is redelivered once its redelivery time passes.
    private async Task DeliverFaultAsync(FaultInfo fault)
    {
        bool delivered;
        try { delivered = await _faults.DispatchAsync(fault, Cts.Token); }
        catch (Exception fex)
        {
            Logger.LogError(fex, "Fault consumer threw for dead-lettered message {MessageId} on channel {Channel}; it will be redelivered",
                fault.MessageId, fault.Channel);
            return;
        }

        if (delivered)
            await MarkFaultDeliveredAsync(fault);
    }

    // ── Disposal ────────────────────────────────────────────────────────────

    /// <summary>Stops the polling loops, waiting up to ten seconds for in-flight batches.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cts.Cancel();
        try { await Task.WhenAll(_pollingTasks).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        await DisposeCoreAsync();
        Cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases transport resources after the polling loops have stopped.</summary>
    private protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
}

/// <summary>What happened to a failed delivery attempt.</summary>
/// <param name="Attempt">The 1-based attempt that failed; the new retry count.</param>
/// <param name="DeadLetter">Whether the message is now dead-lettered.</param>
/// <param name="ScheduledForUtc">Next delivery time (retry) or fault redelivery time (dead-letter).</param>
/// <param name="Error">The error to record.</param>
internal sealed record FailureOutcome(int Attempt, bool DeadLetter, DateTime? ScheduledForUtc, StoredError Error);

/// <summary>
/// Messages claimed by one polling cycle, with the operations that record each outcome.
/// Outcomes become durable at <see cref="CommitAsync"/> (or as they happen, for transports
/// that do not claim inside a transaction).
/// </summary>
internal abstract class ClaimedBatch : IAsyncDisposable
{
    public IReadOnlyList<MessageDto> Messages { get; init; } = Array.Empty<MessageDto>();

    /// <summary>Messages the transport dead-lettered while claiming (Redis reclaim limit).</summary>
    public IReadOnlyList<FaultInfo> DeadLetteredDuringClaim { get; init; } = Array.Empty<FaultInfo>();

    public abstract Task CompleteAsync(MessageDto message);
    public abstract Task FailAsync(MessageDto message, FailureOutcome outcome);
    public virtual Task CommitAsync() => Task.CompletedTask;
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ClaimedBatch Empty { get; } = new EmptyBatch();

    private sealed class EmptyBatch : ClaimedBatch
    {
        public override Task CompleteAsync(MessageDto message) => Task.CompletedTask;
        public override Task FailAsync(MessageDto message, FailureOutcome outcome) => Task.CompletedTask;
    }
}
