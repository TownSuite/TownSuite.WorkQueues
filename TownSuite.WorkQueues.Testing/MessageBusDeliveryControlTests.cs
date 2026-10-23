using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;
using StackExchange.Redis;
using Testcontainers.Redis;
using TownSuite.WorkQueues.Postgres;
using TownSuite.WorkQueues.Redis;
using TownSuite.WorkQueues.Sqlite;
using TownSuite.WorkQueues.SqlServer;

namespace TownSuite.WorkQueues.Testing;

/// <summary>
/// Graceful drain, trace propagation, message size limit, rate limiting, priority and batch
/// publish (TownSuite.WorkQueues#11, #12, #15–#18).
/// </summary>
[TestFixture]
public class MessageBusDeliveryControlTests
{
    // Creates buses over one store: every bus a factory creates sees the same messages.
    private delegate IMessageBus BusFactory(Action<BatchOptions>? configure = null);

    // ── Shared scenarios ──────────────────────────────────────────────────────

    // A drain lets the in-flight message finish without cancelling it and releases the rest of
    // the batch untouched; another bus then delivers them as first attempts.
    private static async Task AssertDrainFinishesInFlight(BusFactory newBus)
    {
        using var spans = new SpanRecorder();
        var consumer = new DrainProbeConsumer(TimeSpan.FromSeconds(2));
        await using (var bus = newBus())
        {
            await bus.PublishBatch(Enumerable.Range(0, 3).Select(i => Order($"drain {i}")));
            bus.Subscribe(consumer);
            await consumer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var stopwatch = Stopwatch.StartNew();
            await bus.DrainAsync(TimeSpan.FromSeconds(10));
            stopwatch.Stop();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)), "Returns once the in-flight message finishes.");
            Assert.That(consumer.Completed, Is.EqualTo(1));
            Assert.That(consumer.Cancelled, Is.Zero, "The in-flight consumer's token is not cancelled.");
            Assert.That(consumer.Calls, Is.EqualTo(1), "No message is started after the drain begins.");
            Assert.That(bus.IsPolling, Is.False);
        }

        await AssertRemainingDeliveredAsFirstAttempts(newBus, spans, expected: 2);
    }

    // A drain that times out cancels the consumer and releases its message without counting an attempt.
    private static async Task AssertDrainTimeoutReleases(BusFactory newBus)
    {
        using var spans = new SpanRecorder();
        var consumer = new DrainProbeConsumer(TimeSpan.FromSeconds(30));
        await using (var bus = newBus())
        {
            bus.Subscribe(consumer);
            await bus.Publish(Order("slow"));
            await consumer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var stopwatch = Stopwatch.StartNew();
            await bus.DrainAsync(TimeSpan.FromSeconds(1));
            stopwatch.Stop();

            Assert.That(stopwatch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)), "Returns soon after the timeout.");
            Assert.That(consumer.Cancelled, Is.EqualTo(1), "The consumer's token is cancelled at the timeout.");
            Assert.That(consumer.Completed, Is.Zero);
        }

        await AssertRemainingDeliveredAsFirstAttempts(newBus, spans, expected: 1);
    }

    private static async Task AssertRemainingDeliveredAsFirstAttempts(BusFactory newBus, SpanRecorder spans, int expected)
    {
        spans.Clear();
        var next = new CountingConsumer();
        await using var bus = newBus();
        Assert.That((await bus.GetQueueStatistics<OrderSubmitted>()).PendingCount, Is.EqualTo(expected),
            "Released messages are pending, not dead-lettered or completed.");
        bus.Subscribe(next);
        await WaitFor(() => next.Count >= expected, timeoutMs: 10000);

        Assert.That(next.Count, Is.EqualTo(expected), "Released messages are delivered again at once.");
        var attempts = spans.Process.Select(s => s.GetTagItem("townsuite.workqueues.delivery_attempt")).ToList();
        Assert.That(attempts, Has.Count.EqualTo(expected));
        Assert.That(attempts, Is.All.EqualTo(1), "Releasing does not count a delivery attempt.");
    }

    // The consume span is a child of the publish span, inside the caller's trace, through a retry
    // and a dead-letter replay.
    private static async Task AssertTracePropagation(BusFactory newBus)
    {
        using var spans = new SpanRecorder();
        var consumer = new TraceCapturingConsumer { FailTimes = 1 };
        await using var bus = newBus(o => { o.RetryDelay = TimeSpan.FromMilliseconds(200); o.MaxRetries = 2; });
        bus.Subscribe(consumer);

        Guid messageId;
        ActivityTraceId requestTrace;
        using (var request = SpanRecorder.TestSource.StartActivity("request"))
        {
            Assert.That(request, Is.Not.Null);
            requestTrace = request!.TraceId;
            messageId = await bus.Publish(Order("traced"), new PublishOptions());
        }

        await WaitFor(() => consumer.Seen.Count >= 2, timeoutMs: 10000);
        var publish = spans.Publish.Single(s => (string?)s.GetTagItem("messaging.message.id") == messageId.ToString());
        Assert.That(publish.TraceId, Is.EqualTo(requestTrace), "The publish span joins the caller's trace.");
        Assert.That(publish.Kind, Is.EqualTo(ActivityKind.Producer));

        Assert.That(consumer.Seen, Has.Count.EqualTo(2), "Delivered, failed once, retried.");
        foreach (var (traceId, parentSpanId) in consumer.Seen)
        {
            Assert.That(traceId, Is.EqualTo(requestTrace), "Activity.Current in the consumer is in the publisher's trace.");
            Assert.That(parentSpanId, Is.EqualTo(publish.SpanId), "The consume span's parent is the publish span.");
        }

        var process = spans.ProcessFor(messageId);
        Assert.That(process.Select(s => s.GetTagItem("townsuite.workqueues.outcome")), Is.EqualTo(new[] { "retried", "processed" }));
        Assert.That(process[0].Status, Is.EqualTo(ActivityStatusCode.Error));
        Assert.That(process[0].Events.Select(e => e.Name), Does.Contain("exception"));
        Assert.That(process.Select(s => s.Kind), Is.All.EqualTo(ActivityKind.Consumer));

        // Dead-letter, then replay: the replayed delivery is still in the original trace.
        consumer.Seen.Clear();
        consumer.FailTimes = int.MaxValue;
        var dead = await bus.Publish(Order("replayed"), new PublishOptions());
        await WaitFor(async () => (await bus.GetQueueStatistics<OrderSubmitted>()).DeadLetteredCount == 1, timeoutMs: 10000);
        var deadTrace = spans.ProcessFor(dead)[0].TraceId;

        consumer.Seen.Clear();
        consumer.FailTimes = 0;
        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(dead), Is.True);
        await WaitFor(() => consumer.Seen.Count >= 1, timeoutMs: 10000);
        Assert.That(consumer.Seen.Single().TraceId, Is.EqualTo(deadTrace), "Replay keeps the trace context.");
    }

    // Oversized messages are rejected at publish and, if already stored, dead-lettered unread.
    private static async Task AssertSizeGuard(BusFactory newBus)
    {
        var big = Order(new string('x', 500));
        await using (var limited = newBus(o => o.MaxMessageSize = 200))
        {
            await limited.Publish(Order("small"));
            var ex = Assert.ThrowsAsync<MessageTooLargeException>(() => limited.Publish(big));
            Assert.That(ex!.Limit, Is.EqualTo(200));
            Assert.That(ex.Size, Is.GreaterThan(500));
            Assert.That(ex.Channel, Is.EqualTo(typeof(OrderSubmitted).FullName));
            Assert.ThrowsAsync<MessageTooLargeException>(() => limited.PublishBatch(new[] { Order("ok"), big }));

            Assert.That((await limited.GetQueueStatistics<OrderSubmitted>()).PendingCount, Is.EqualTo(1),
                "Only the small message was stored; a batch with an oversized message stores nothing.");
        }

        // Published by a bus without the limit, then claimed by one with it.
        await using (var unlimited = newBus()) await unlimited.Publish(big);

        var consumer = new CountingConsumer();
        var faults = new CapturingFaultConsumer<OrderSubmitted>();
        await using (var limited = newBus(o => o.MaxMessageSize = 200))
        {
            limited.Subscribe(consumer);
            limited.SubscribeFault(faults);
            await WaitFor(() => faults.Received != null && consumer.Count >= 1, timeoutMs: 10000);
        }

        Assert.That(consumer.Count, Is.EqualTo(1), "Only the small message reaches the consumer.");
        Assert.That(faults.Received, Is.Not.Null);
        Assert.That(faults.Received!.ExceptionType, Is.EqualTo(typeof(MessageTooLargeException).FullName));
        Assert.That(faults.Received.NonRetryable, Is.True);
        Assert.That(faults.Received.AttemptCount, Is.EqualTo(1), "Dead-lettered on the first claim, not retried.");
    }

    // 25 messages at 10 per second take about 2.4 s (one token to start, then one every 100 ms).
    private static async Task AssertRateLimit(BusFactory newBus)
    {
        var consumer = new CountingConsumer();
        await using var bus = newBus(o => { o.MessagesPerSecond = 10; o.MaxConcurrency = 2; });
        await bus.PublishBatch(Enumerable.Range(0, 25).Select(i => Order($"rate {i}")));

        var stopwatch = Stopwatch.StartNew();
        bus.Subscribe(consumer);
        await WaitFor(() => consumer.Count >= 25, timeoutMs: 15000);
        stopwatch.Stop();

        Assert.That(consumer.Count, Is.EqualTo(25));
        Assert.That(stopwatch.Elapsed, Is.GreaterThan(TimeSpan.FromSeconds(2)), "Delivery is held to the rate.");
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6)));
    }

    // Lower priority values are delivered first; equal priorities oldest first.
    private static async Task AssertPriority(BusFactory newBus)
    {
        var consumer = new OrderRecordingConsumer();
        await using var bus = newBus(o => o.MaxBatchSize = 2);
        await bus.PublishBatch(new[] { Order("p5 a"), Order("p5 b") }, new PublishOptions { Priority = 5 });
        await bus.Publish(Order("p0 a"), new PublishOptions());
        await bus.Publish(Order("p0 b"), new PublishOptions());
        await bus.Publish(Order("p-1"), new PublishOptions { Priority = -1 });

        bus.Subscribe(consumer);
        await WaitFor(() => consumer.Names.Count >= 5, timeoutMs: 10000);

        Assert.That(consumer.Names, Is.EqualTo(new[] { "p-1", "p0 a", "p0 b", "p5 a", "p5 b" }));
    }

    // A batch is published all or none, in order, and returns ids in input order.
    private static async Task AssertBatchPublish(BusFactory newBus, int count)
    {
        var consumer = new OrderRecordingConsumer();
        await using var bus = newBus(o => o.MaxBatchSize = 200);

        Assert.That(await bus.PublishBatch(Array.Empty<OrderSubmitted>()), Is.Empty);

        var names = Enumerable.Range(0, count).Select(i => $"batch {i:D5}").ToArray();
        var ids = await bus.PublishBatch(names.Select(Order));
        Assert.That(ids, Has.Count.EqualTo(count));
        Assert.That(ids.Distinct().Count(), Is.EqualTo(count));
        Assert.That((await bus.GetQueueStatistics<OrderSubmitted>()).PendingCount, Is.EqualTo(count));

        bus.Subscribe(consumer);
        await WaitFor(() => consumer.Names.Count >= count, timeoutMs: 30000);
        Assert.That(consumer.Names, Is.EqualTo(names), "Delivered in input order.");
        Assert.That(consumer.Ids, Is.EqualTo(ids), "Ids are returned in input order.");
    }

    // A batch on the caller's transaction is published only if it commits.
    private static async Task AssertTransactionalBatch(BusFactory newBus, Func<DbConnectionPair> open)
    {
        var consumer = new OrderRecordingConsumer();
        await using var bus = newBus();
        bus.Subscribe(consumer);

        await using (var pair = open())
        {
            await bus.PublishBatch(new[] { Order("rolled back 1"), Order("rolled back 2") }, pair.Connection, pair.Begin());
            await pair.Transaction!.RollbackAsync();
        }
        await using (var pair = open())
        {
            await bus.PublishBatch(new[] { Order("committed 1"), Order("committed 2") }, pair.Connection, pair.Begin());
            await pair.Transaction!.CommitAsync();
        }

        await WaitFor(() => consumer.Names.Count >= 2, timeoutMs: 10000);
        await Task.Delay(300);
        Assert.That(consumer.Names, Is.EqualTo(new[] { "committed 1", "committed 2" }));
    }

    // ── Unit tests (no store) ─────────────────────────────────────────────────

    [Test]
    public async Task RateLimiter_HoldsToTheRate_AndReturnsUnusedTokens()
    {
        Assert.That(MessageRateLimiter.For(new BatchOptions()), Is.Null, "0 means no limit.");

        var limiter = MessageRateLimiter.For(new BatchOptions { MessagesPerSecond = 4 })!;
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) await limiter.WaitAsync(CancellationToken.None);
        Assert.That(stopwatch.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(1)).Within(TimeSpan.FromMilliseconds(300)),
            "One token to start, then one every 250 ms.");

        Assert.That(limiter.TryTake(10), Is.Zero);
        limiter.Return(2);
        Assert.That(limiter.TryTake(10), Is.EqualTo(2));
    }

    [Test]
    public async Task RateLimiter_BelowOnePerSecond()
    {
        var limiter = MessageRateLimiter.For(new BatchOptions { MessagesPerSecond = 0.5 })!;
        var stopwatch = Stopwatch.StartNew();
        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);
        Assert.That(stopwatch.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(2)).Within(TimeSpan.FromMilliseconds(300)));
    }

    // ── SQLite ────────────────────────────────────────────────────────────────

    [Test] public Task Sqlite_DrainFinishesInFlight() => WithSqlite(AssertDrainFinishesInFlight);
    [Test] public Task Sqlite_DrainTimeoutReleases() => WithSqlite(AssertDrainTimeoutReleases);
    [Test] public Task Sqlite_TracePropagation() => WithSqlite(AssertTracePropagation);
    [Test] public Task Sqlite_SizeGuard() => WithSqlite(AssertSizeGuard);
    [Test] public Task Sqlite_RateLimit() => WithSqlite(AssertRateLimit);
    [Test] public Task Sqlite_Priority() => WithSqlite(AssertPriority);
    [Test] public Task Sqlite_BatchPublish() => WithSqlite(f => AssertBatchPublish(f, 1500));

    [Test]
    public Task Sqlite_TransactionalBatch() => WithSqlite(f =>
        AssertTransactionalBatch(f, () => new DbConnectionPair(new SqliteConnection(_sqliteConnectionString))));

    [Test]
    public async Task Sqlite_DisposeDrains()
    {
        await WithSqlite(async newBus =>
        {
            var consumer = new DrainProbeConsumer(TimeSpan.FromSeconds(1));
            var bus = newBus(o => o.DrainTimeout = TimeSpan.FromSeconds(10));
            bus.Subscribe(consumer);
            await bus.Publish(Order("dispose"));
            await consumer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await bus.DisposeAsync();

            Assert.That(consumer.Completed, Is.EqualTo(1), "Disposing waits for the in-flight message.");
            Assert.That(consumer.Cancelled, Is.Zero);
            await using var check = newBus();
            Assert.That((await check.GetQueueStatistics<OrderSubmitted>()).PendingCount, Is.Zero);
        });
    }

    private static string? _sqliteConnectionString;

    private static async Task WithSqlite(Func<BusFactory, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wq-control-{Guid.NewGuid()}.db");
        _sqliteConnectionString = $"Data Source={path}";
        SqliteTransportOptions Options(Action<BatchOptions>? configure)
        {
            var o = new SqliteTransportOptions
            {
                ConnectionString     = _sqliteConnectionString,
                ContinuousPolling    = true,
                MaxBatchSize         = 10,
                MaxWaitTime          = TimeSpan.FromMilliseconds(100),
                FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
            };
            configure?.Invoke(o);
            return o;
        }

        try
        {
            var sp = new ServiceCollection().AddSingleton(Options(null)).AddLogging().BuildServiceProvider();
            await new SqliteMigrationHostedService(sp, sp.GetRequiredService<ILogger<SqliteMigrationHostedService>>())
                .StartAsync(CancellationToken.None);

            await test(configure => new SqliteMessageBus(Options(configure), Moq.Mock.Of<ILogger<SqliteMessageBus>>()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    // ── SQL Server ────────────────────────────────────────────────────────────

    [Test]
    public async Task SqlServer_DeliveryControl()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("mssql");
        await wrapper.StartAsync();
        var cs = wrapper.Container.GetConnectionString();

        BusFactory Factory(TimeSpan? lease) => configure =>
        {
            var o = new SqlServerTransportOptions
            {
                ConnectionString = cs, Schema = "dbo", ContinuousPolling = true, MaxBatchSize = 10, ClaimLease = lease,
                MaxWaitTime = TimeSpan.FromMilliseconds(100), FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
            };
            configure?.Invoke(o);
            return new SqlServerMessageBus(o, Moq.Mock.Of<ILogger<SqlServerMessageBus>>());
        };
        async Task Clear() { await using var cn = new SqlConnection(cs); await cn.ExecuteAsync("DELETE FROM dbo.workqueue"); }

        foreach (var lease in new TimeSpan?[] { null, TimeSpan.FromSeconds(30) })
        {
            await AssertDrainFinishesInFlight(Factory(lease)); await Clear();
            await AssertDrainTimeoutReleases(Factory(lease)); await Clear();
        }
        await AssertTracePropagation(Factory(null)); await Clear();
        await AssertSizeGuard(Factory(null)); await Clear();
        await AssertRateLimit(Factory(null)); await Clear();
        await AssertPriority(Factory(null)); await Clear();
        await AssertBatchPublish(Factory(null), 2500); await Clear();   // more than one 1000-row chunk
        await AssertTransactionalBatch(Factory(null), () => new DbConnectionPair(new SqlConnection(cs)));
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    [Test]
    public async Task Postgres_DeliveryControl()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("postgres");
        await wrapper.StartAsync();
        var cs = wrapper.Container.GetConnectionString();

        BusFactory Factory(TimeSpan? lease) => configure =>
        {
            var o = new SqlTransportOptions
            {
                ConnectionString = cs, Schema = "public", ContinuousPolling = true, MaxBatchSize = 10, ClaimLease = lease,
                MaxWaitTime = TimeSpan.FromMilliseconds(100), FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
            };
            configure?.Invoke(o);
            return new PostgresMessageBus(o, Moq.Mock.Of<ILogger<PostgresMessageBus>>());
        };
        async Task Clear() { await using var cn = new NpgsqlConnection(cs); await cn.ExecuteAsync("DELETE FROM public.workqueue"); }

        foreach (var lease in new TimeSpan?[] { null, TimeSpan.FromSeconds(30) })
        {
            await AssertDrainFinishesInFlight(Factory(lease)); await Clear();
            await AssertDrainTimeoutReleases(Factory(lease)); await Clear();
        }
        await AssertTracePropagation(Factory(null)); await Clear();
        await AssertSizeGuard(Factory(null)); await Clear();
        await AssertRateLimit(Factory(null)); await Clear();
        await AssertPriority(Factory(null)); await Clear();
        await AssertBatchPublish(Factory(null), 2500); await Clear();
        await AssertTransactionalBatch(Factory(null), () => new DbConnectionPair(new NpgsqlConnection(cs)));
    }

    // ── Redis ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Redis_DeliveryControl()
    {
        await using var container = new RedisBuilder().Build();
        await container.StartAsync();
        using var mux = ConnectionMultiplexer.Connect(container.GetConnectionString());
        int prefix = 0;

        // A fresh key prefix per scenario stands in for clearing the store.
        BusFactory Factory()
        {
            var keyPrefix = $"control{++prefix}";
            return configure =>
            {
                var o = new RedisOptions
                {
                    KeyPrefix = keyPrefix, ConsumerGroup = "g", ConsumerName = "c", MaxBatchSize = 10,
                    MaxWaitTime = TimeSpan.FromMilliseconds(100), ReclaimIdleTime = TimeSpan.FromSeconds(30),
                    FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
                };
                configure?.Invoke(o);
                return new RedisMessageBus(mux, o, Moq.Mock.Of<ILogger<RedisMessageBus>>());
            };
        }

        await AssertDrainFinishesInFlight(Factory());
        await AssertDrainTimeoutReleases(Factory());
        await AssertTracePropagation(Factory());
        await AssertSizeGuard(Factory());
        await AssertRateLimit(Factory());
        await AssertBatchPublish(Factory(), 1500);

        await using var bus = Factory()();
        Assert.ThrowsAsync<NotSupportedException>(() => bus.Publish(Order("prioritised"), new PublishOptions { Priority = 1 }));

        // Batch on a Redis MULTI: only an executed transaction publishes.
        var redisBus = (RedisMessageBus)bus;
        var consumer = new OrderRecordingConsumer();
        bus.Subscribe(consumer);
        redisBus.PublishBatch(new[] { Order("never executed") }, mux.GetDatabase().CreateTransaction());
        var tran = mux.GetDatabase().CreateTransaction();
        var ids = redisBus.PublishBatch(new[] { Order("executed 1"), Order("executed 2") }, tran);
        Assert.That(await tran.ExecuteAsync(), Is.True);
        await WaitFor(() => consumer.Names.Count >= 2, timeoutMs: 5000);
        await Task.Delay(300);
        Assert.That(consumer.Names, Is.EqualTo(new[] { "executed 1", "executed 2" }));
        Assert.That(consumer.Ids, Is.EqualTo(ids));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OrderSubmitted Order(string name) => new() { OrderId = Guid.NewGuid(), ProductName = name };

    private static async Task WaitFor(Func<bool> predicate, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static async Task WaitFor(Func<Task<bool>> predicate, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    // A caller's connection and the transaction begun on it.
    private sealed class DbConnectionPair(System.Data.Common.DbConnection connection) : IAsyncDisposable
    {
        public System.Data.Common.DbConnection Connection { get; } = connection;
        public System.Data.Common.DbTransaction? Transaction { get; private set; }

        public System.Data.Common.DbTransaction Begin()
        {
            Connection.Open();
            return Transaction = Connection.BeginTransaction();
        }

        public async ValueTask DisposeAsync()
        {
            if (Transaction != null) await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    // Records the bus's spans while it is alive.
    private sealed class SpanRecorder : IDisposable
    {
        private const string TestSourceName = "TownSuite.WorkQueues.Testing";
        public static readonly ActivitySource TestSource = new(TestSourceName);

        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _stopped = new();

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == WorkQueueTracing.ActivitySourceName || s.Name == TestSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => _stopped.Enqueue(a)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Publish => Of(ActivityKind.Producer);
        public IReadOnlyList<Activity> Process => Of(ActivityKind.Consumer);

        public IReadOnlyList<Activity> ProcessFor(Guid messageId) =>
            Process.Where(a => (string?)a.GetTagItem("messaging.message.id") == messageId.ToString()).ToList();

        public void Clear() => _stopped.Clear();

        private IReadOnlyList<Activity> Of(ActivityKind kind) =>
            _stopped.Where(a => a.Source.Name == WorkQueueTracing.ActivitySourceName && a.Kind == kind).ToList();

        public void Dispose() => _listener.Dispose();
    }
}

// Waits, honouring the bus's token; records whether it finished or was cancelled.
internal class DrainProbeConsumer(TimeSpan delay) : IConsumer<OrderSubmitted>
{
    private int _calls, _completed, _cancelled;
    public int Calls => _calls;
    public int Completed => _completed;
    public int Cancelled => _cancelled;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        Interlocked.Increment(ref _calls);
        Started.TrySetResult();
        try
        {
            await Task.Delay(delay, context.CancellationToken);
            Interlocked.Increment(ref _completed);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
    }
}

internal class TraceCapturingConsumer : IConsumer<OrderSubmitted>
{
    private int _calls;
    public int FailTimes { get; set; }
    public ConcurrentQueue<(ActivityTraceId TraceId, ActivitySpanId ParentSpanId)> Seen { get; } = new();

    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        var current = Activity.Current!;
        Seen.Enqueue((current.TraceId, current.ParentSpanId));
        if (Interlocked.Increment(ref _calls) <= FailTimes)
            throw new InvalidOperationException("trace probe failure");
        return Task.CompletedTask;
    }
}

internal class OrderRecordingConsumer : IConsumer<OrderSubmitted>
{
    private readonly object _lock = new();
    private readonly List<string> _names = new();
    private readonly List<Guid> _ids = new();

    public IReadOnlyList<string> Names { get { lock (_lock) return _names.ToList(); } }
    public IReadOnlyList<Guid> Ids { get { lock (_lock) return _ids.ToList(); } }

    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        lock (_lock)
        {
            _names.Add(context.Message.ProductName);
            _ids.Add(context.MessageId);
        }
        return Task.CompletedTask;
    }
}
