using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TownSuite.WorkQueues;

/// <summary>
/// Shared implementation of <see cref="IMessageBus"/> used by every built-in transport:
/// subscriptions, the polling loops, dispatch, the retry / dead-letter decision, message
/// expiry, size and rate limits, draining, at-least-once fault delivery, metrics and tracing.
/// A transport supplies only the storage operations (insert, claim, complete, fail, release,
/// fault bookkeeping and queries).
/// </summary>
public abstract class MessageBusBase : IMessageBus
{
    private readonly ConcurrentDictionary<string, (Type Type, ConcurrentDictionary<object, Func<object, Guid, DateTimeOffset, Task>> Handlers)> _handlers = new();
    private readonly FaultRegistry _faults = new();
    private readonly IntervalGate _faultGate;
    private readonly BatchOptions _batchOptions;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string _transport;
    private readonly MessageRateLimiter? _rateLimiter;
    private readonly CancellationTokenSource _stopClaiming = new();
    private Task[] _pollingTasks = Array.Empty<Task>();
    private int _disposed;

    /// <summary>
    /// Cancelled when consumers must stop: the token they receive. Claiming stops earlier, when a
    /// drain starts; this is cancelled only once the drain timeout passes.
    /// </summary>
    private protected readonly CancellationTokenSource Cts = new();
    private protected readonly ILogger Logger;

    private protected MessageBusBase(BatchOptions options, ILogger logger, IServiceProvider? serviceProvider, string transport)
    {
        _batchOptions    = options ?? throw new ArgumentNullException(nameof(options));
        Logger           = logger  ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider;
        _transport       = transport;
        _faultGate       = IntervalGate.ForFaultRedelivery(options);
        _rateLimiter     = MessageRateLimiter.For(options);
    }

    /// <summary>
    /// Starts the polling loops. Transports call this last in their constructor, once their own
    /// fields are assigned.
    /// </summary>
    private protected void StartPolling()
    {
        // Yield to the caller so Subscribe() calls made immediately after construction
        // are registered before the first poll cycle runs. The loops must not inherit the
        // constructing caller's activity, or every delivery would join that caller's trace.
        _pollingTasks = Enumerable.Range(0, Math.Max(1, _batchOptions.MaxConcurrency))
            .Select(_ => Task.Run(async () =>
            {
                Activity.Current = null;
                await Task.Yield();
                await PollAsync();
            }))
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
        await Publish(message, PublishOptions.None, cancellationToken);

    /// <inheritdoc />
    public virtual async Task Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default) =>
        await Publish(message, new PublishOptions { DeliverAfter = deliverAfter }, cancellationToken);

    /// <inheritdoc />
    public async Task<Guid> Publish<T>(T message, PublishOptions options, CancellationToken cancellationToken = default) =>
        (await PublishBatch(new[] { message }, options, cancellationToken))[0];

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> PublishBatch<T>(IEnumerable<T> messages, PublishOptions? options = null,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(messages, options, batch => InsertAsync(batch, cancellationToken));

    /// <inheritdoc />
    public async Task<Guid> Publish<T>(T message, DbConnection connection, DbTransaction? transaction,
        PublishOptions? options = null, CancellationToken cancellationToken = default) =>
        (await PublishBatch(new[] { message }, connection, transaction, options, cancellationToken))[0];

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> PublishBatch<T>(IEnumerable<T> messages, DbConnection connection,
        DbTransaction? transaction, PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!SupportsTransactionalPublish)
            throw new NotSupportedException($"{GetType().Name} does not support transactional publish.");
        return PublishCoreAsync(messages, options,
            batch => InsertAsync(connection, transaction, batch, cancellationToken));
    }

    /// <summary>
    /// Serialises, checks and traces <paramref name="messages"/>, then stores them with
    /// <paramref name="insert"/>. Every publish overload, in every transport, goes through here.
    /// </summary>
    private protected async Task<IReadOnlyList<Guid>> PublishCoreAsync<T>(IEnumerable<T> messages, PublishOptions? options,
        Func<IReadOnlyList<OutgoingMessage>, Task> insert)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= PublishOptions.None;
        ValidatePublishOptions(options);

        var channel = ChannelName<T>();
        var payloads = messages.Select(m => JsonSerializer.Serialize(m)).ToList();
        if (payloads.Count == 0) return Array.Empty<Guid>();

        if (_batchOptions.MaxMessageSize is { } maxSize)
        {
            foreach (var payload in payloads)
                if (Encoding.UTF8.GetByteCount(payload) is var size && size > maxSize)
                    throw new MessageTooLargeException(channel, size, maxSize);
        }

        using var activity = WorkQueueTracing.StartPublish(_transport, channel, payloads.Count);
        var traceContext = WorkQueueTracing.CaptureContext(activity);
        var outgoing = payloads.Select(p => new OutgoingMessage(channel, p, Guid.NewGuid(), options, traceContext)).ToArray();
        if (outgoing.Length == 1) activity?.SetTag("messaging.message.id", outgoing[0].MessageId.ToString());

        try
        {
            await insert(outgoing);
        }
        catch (Exception ex)
        {
            WorkQueueTracing.RecordException(activity, ex);
            throw;
        }

        WorkQueueMetrics.RecordPublished(_transport, channel, outgoing.Length);
        return outgoing.Select(m => m.MessageId).ToArray();
    }

    /// <summary>Throws for <see cref="PublishOptions"/> the transport cannot honour.</summary>
    private protected virtual void ValidatePublishOptions(PublishOptions options) { }

    /// <summary>Inserts serialised messages, all or none.</summary>
    private protected abstract Task InsertAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken);

    /// <summary>Whether the transport overrides the connection/transaction <c>InsertAsync</c>.</summary>
    private protected virtual bool SupportsTransactionalPublish => false;

    /// <summary>
    /// Inserts serialised messages using the caller's connection and transaction (the outbox
    /// pattern). Transports that set <see cref="SupportsTransactionalPublish"/> override it.
    /// </summary>
    private protected virtual Task InsertAsync(DbConnection connection, DbTransaction? transaction,
        IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not support transactional publish.");

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
        var stopping = _stopClaiming.Token;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                int processed = await ProcessBatchAsync();
                if (!stopping.IsCancellationRequested && _faultGate.TryEnter())
                    processed += await RedeliverFaultsAsync();

                if (processed == 0 && !_batchOptions.ContinuousPolling)
                    await Task.Delay(_batchOptions.MaxWaitTime, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in {Bus} polling loop", GetType().Name);
                try { await Task.Delay(1000, stopping); } catch (OperationCanceledException) { }
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

        // With a rate limit, tokens for the whole claim are taken before claiming, so no claimed
        // message waits for a token (and no lease runs down) unless the transport over-claims.
        int maxMessages = _batchOptions.MaxBatchSize;
        int prepaid = 0;
        if (_rateLimiter != null)
        {
            await _rateLimiter.WaitAsync(_stopClaiming.Token);
            prepaid = 1 + _rateLimiter.TryTake(maxMessages - 1);
            maxMessages = prepaid;
        }

        ClaimedBatch claimed;
        try
        {
            claimed = await ClaimAsync(channels, maxMessages, _stopClaiming.Token);
        }
        catch
        {
            _rateLimiter?.Return(prepaid);
            throw;
        }
        await using var batch = claimed;
        _rateLimiter?.Return(prepaid - Math.Min(prepaid, batch.Messages.Count));

        // Faults are delivered only after the batch's outcomes are durable, so a failed commit
        // never reports a dead-letter that did not happen.
        var faults = new List<FaultInfo>(batch.DeadLetteredDuringClaim);
        bool draining = false;

        for (int i = 0; i < batch.Messages.Count; i++)
        {
            var msg = batch.Messages[i];

            if (!draining && _rateLimiter != null && i >= prepaid)
            {
                try { await _rateLimiter.WaitAsync(_stopClaiming.Token); }
                catch (OperationCanceledException) { draining = true; }
            }

            // Once a drain starts, messages not yet started go back to the queue untouched.
            if (draining || _stopClaiming.IsCancellationRequested)
            {
                draining = true;
                await batch.ReleaseAsync(msg);
                continue;
            }

            using var activity = WorkQueueTracing.StartProcess(_transport, msg);
            var stopwatch = Stopwatch.StartNew();
            Exception? error = RejectBeforeDispatch(msg);
            if (error == null)
            {
                try
                {
                    await DispatchAsync(msg);
                }
                catch (OperationCanceledException) when (Cts.IsCancellationRequested)
                {
                    // The drain timed out and cancelled this consumer: release, do not count an attempt.
                    WorkQueueTracing.SetOutcome(activity, "released");
                    await batch.ReleaseAsync(msg);
                    draining = true;
                    continue;
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
                WorkQueueTracing.SetOutcome(activity, "processed");
                continue;
            }

            int attempt = msg.RetryCount + 1;
            bool expired = error is MessageExpiredException;
            bool tooLarge = error is MessageTooLargeException;
            bool nonRetryable = tooLarge || (!expired && !_batchOptions.ShouldRetry(error));
            bool deadLetter = expired || nonRetryable || attempt >= _batchOptions.MaxRetries;
            var stored = StoredError.From(error, nonRetryable);
            WorkQueueTracing.RecordException(activity, error);

            // A dead-lettered message reuses its scheduled time as the time its fault may be
            // redelivered, in case the fault consumer below does not run or does not finish.
            var holdFor = deadLetter ? _batchOptions.FaultRedeliveryDelay : _batchOptions.GetRetryDelay(attempt);
            DateTime? scheduledFor = holdFor > TimeSpan.Zero ? DateTime.UtcNow.Add(holdFor) : null;

            await batch.FailAsync(msg, new FailureOutcome(attempt, deadLetter, scheduledFor, stored));

            if (deadLetter)
            {
                bool consumersRan = !expired && !tooLarge;
                WorkQueueMetrics.RecordDeadLettered(_transport, msg.Channel, consumersRan ? stopwatch.Elapsed : null);
                if (expired) WorkQueueMetrics.RecordExpired(_transport, msg.Channel);
                WorkQueueTracing.SetOutcome(activity, expired ? "expired" : "deadlettered");
                faults.Add(new FaultInfo(msg.Channel, msg.Payload, msg.MessageId, attempt, DateTimeOffset.UtcNow,
                    stored, IsRedelivery: false) { FaultKey = msg.FaultKey });
            }
            else
            {
                WorkQueueMetrics.RecordRetried(_transport, msg.Channel, stopwatch.Elapsed);
                WorkQueueTracing.SetOutcome(activity, "retried");
            }
        }

        await batch.CommitAsync();

        foreach (var fault in faults)
            await DeliverFaultAsync(fault);

        return batch.Messages.Count + batch.DeadLetteredDuringClaim.Count;
    }

    // Messages that are dead-lettered without calling consumers.
    private Exception? RejectBeforeDispatch(MessageDto msg)
    {
        if (msg.ExpiresAtUtc is { } expiresAt && expiresAt <= DateTime.UtcNow)
            return new MessageExpiredException(new DateTimeOffset(expiresAt, TimeSpan.Zero));

        if (_batchOptions.MaxMessageSize is { } maxSize && Encoding.UTF8.GetByteCount(msg.Payload) is var size && size > maxSize)
        {
            Logger.LogWarning("Message {MessageId} on channel {Channel} is {Size} bytes, over MaxMessageSize {Limit}; dead-lettering it",
                msg.MessageId, msg.Channel, size, maxSize);
            return new MessageTooLargeException(msg.Channel, size, maxSize);
        }

        return null;
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

        var due = await ClaimDueFaultsAsync(channels, _batchOptions.MaxBatchSize, _batchOptions.FaultRedeliveryDelay, _stopClaiming.Token);
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

    // How long a drain waits, after cancelling consumers, for them to stop and release their messages.
    private static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 2) return;
        _stopClaiming.Cancel();

        var polling = Task.WhenAll(_pollingTasks);
        try
        {
            await polling.WaitAsync(timeout, cancellationToken);
            return;
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }

        Logger.LogWarning("{Bus} drain did not finish within {Timeout}; cancelling in-flight consumers", GetType().Name, timeout);
        Cts.Cancel();
        try
        {
            await polling.WaitAsync(ReleaseGrace);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning(
                "{Bus} consumers did not stop after cancellation; their messages are delivered again once the claim lease expires or the claim transaction ends",
                GetType().Name);
        }
    }

    /// <summary>
    /// Drains the bus (<see cref="DrainAsync"/>) for up to <see cref="BatchOptions.DrainTimeout"/>,
    /// then releases transport resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        await DrainAsync(_batchOptions.DrainTimeout);
        Cts.Cancel();
        await DisposeCoreAsync();
        Volatile.Write(ref _disposed, 2);
        _stopClaiming.Dispose();
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

/// <summary>A serialised message about to be inserted by a transport.</summary>
/// <param name="Channel">The message type's channel.</param>
/// <param name="Payload">The JSON payload.</param>
/// <param name="MessageId">The id assigned to the message.</param>
/// <param name="Options">Delivery time, expiry and priority (shared by every message in a batch).</param>
/// <param name="TraceContext">The W3C trace context to store, if any (see <see cref="WorkQueueTracing"/>).</param>
internal sealed record OutgoingMessage(string Channel, string Payload, Guid MessageId, PublishOptions Options, string? TraceContext);

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

    /// <summary>
    /// Gives a claimed message back without recording an attempt, so it can be claimed again at
    /// once. Used when a drain stops a batch part-way. In transaction mode this is a no-op: the
    /// message's row is left unchanged, and committing the batch releases its lock.
    /// </summary>
    public abstract Task ReleaseAsync(MessageDto message);
    public virtual Task CommitAsync() => Task.CompletedTask;
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ClaimedBatch Empty { get; } = new EmptyBatch();

    private sealed class EmptyBatch : ClaimedBatch
    {
        public override Task CompleteAsync(MessageDto message) => Task.CompletedTask;
        public override Task FailAsync(MessageDto message, FailureOutcome outcome) => Task.CompletedTask;
        public override Task ReleaseAsync(MessageDto message) => Task.CompletedTask;
    }
}
