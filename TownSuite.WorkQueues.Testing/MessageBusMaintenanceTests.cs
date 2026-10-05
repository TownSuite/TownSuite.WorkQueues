using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;
using StackExchange.Redis;
using Testcontainers.Redis;
using TownSuite.WorkQueues.HealthChecks;
using TownSuite.WorkQueues.Postgres;
using TownSuite.WorkQueues.Redis;
using TownSuite.WorkQueues.Sqlite;
using TownSuite.WorkQueues.SqlServer;

namespace TownSuite.WorkQueues.Testing;

/// <summary>
/// Message expiry, purging, lease-based claiming, Redis scheduled delivery / backoff /
/// transactional publish, health checks, queue gauges and Postgres UTC timestamps.
/// </summary>
[TestFixture]
public class MessageBusMaintenanceTests
{
    // ── Shared scenarios ──────────────────────────────────────────────────────

    // A message that expires before any consumer subscribes is dead-lettered without being
    // delivered, and its fault says so.
    private static async Task AssertExpiry(IMessageBus bus)
    {
        var consumer = new CountingConsumer();
        var faults   = new CapturingFaultConsumer<OrderSubmitted>();

        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "expired" },
            new PublishOptions { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1) });
        var liveId = await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "live" },
            new PublishOptions { TimeToLive = TimeSpan.FromMinutes(5) });

        bus.Subscribe(consumer);
        bus.SubscribeFault(faults);

        await WaitFor(() => faults.Received != null && consumer.Count >= 1, timeoutMs: 10000);

        Assert.That(consumer.Count, Is.EqualTo(1), "Only the unexpired message is delivered.");
        Assert.That(faults.Received, Is.Not.Null);
        Assert.That(faults.Received!.Expired, Is.True);
        Assert.That(faults.Received.OriginalMessage.ProductName, Is.EqualTo("expired"));
        Assert.That(faults.Received.ExceptionType, Is.EqualTo(typeof(MessageExpiredException).FullName));
        Assert.That(liveId, Is.Not.EqualTo(Guid.Empty));

        var dead = (await bus.GetDeadLettered<OrderSubmitted>()).Single();
        Assert.That(dead.Expired, Is.True);
    }

    // Processed messages and old dead-letters are purged; recent ones are kept.
    private static async Task AssertPurge(IMessageBus bus)
    {
        var consumer = new CountingConsumer();
        bus.Subscribe(consumer);
        for (int i = 0; i < 3; i++)
            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = $"purge {i}" });
        await WaitFor(() => consumer.Count >= 3, timeoutMs: 10000);
        await Task.Delay(300);

        Assert.That(await bus.PurgeProcessed(DateTimeOffset.UtcNow.AddMinutes(-5)), Is.Zero,
            "Nothing was processed before the cut-off.");
        Assert.That(await bus.PurgeProcessed(DateTimeOffset.UtcNow.AddMinutes(1)), Is.EqualTo(3));
        Assert.That(await bus.PurgeProcessed(DateTimeOffset.UtcNow.AddMinutes(1)), Is.Zero);
    }

    private static async Task AssertPurgeDeadLettered(IMessageBus bus)
    {
        bus.Subscribe(new ArgumentThrowingConsumer());
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "dead" });
        await WaitFor(async () => (await bus.GetQueueStatistics<OrderSubmitted>()).DeadLetteredCount == 1, timeoutMs: 10000);

        Assert.That(await bus.PurgeDeadLettered<OrderSubmitted>(DateTimeOffset.UtcNow.AddMinutes(-5)), Is.Zero);
        Assert.That(await bus.PurgeDeadLettered<OrderSubmitted>(DateTimeOffset.UtcNow.AddMinutes(1)), Is.EqualTo(1));

        var stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.DeadLetteredCount, Is.Zero);
        Assert.That(stats.PendingFaultCount, Is.Zero, "Pending faults for purged dead-letters are removed too.");
    }

    // With a lease, a consumer that outlives it sees the message delivered again; the first
    // delivery's late outcome is ignored.
    private static async Task AssertLeaseExpiryRedelivers(IMessageBus bus)
    {
        var consumer = new SlowFirstCallConsumer(TimeSpan.FromSeconds(3));
        bus.Subscribe(consumer);
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "lease" });

        await WaitFor(() => consumer.Calls >= 2, timeoutMs: 10000);
        Assert.That(consumer.Calls, Is.GreaterThanOrEqualTo(2), "The message is redelivered once its lease expires.");
        await WaitFor(async () => (await bus.GetQueueStatistics<OrderSubmitted>()).PendingCount == 0, timeoutMs: 6000);
        Assert.That((await bus.GetQueueStatistics<OrderSubmitted>()).PendingCount, Is.Zero);
    }

    // ── SQLite ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sqlite_Expiry() => await WithSqlite(o => { }, AssertExpiry);

    [Test]
    public async Task Sqlite_PurgeProcessed() => await WithSqlite(o => { }, AssertPurge);

    [Test]
    public async Task Sqlite_PurgeDeadLettered() => await WithSqlite(o => o.MaxRetries = 1, AssertPurgeDeadLettered);

    [Test]
    public async Task Sqlite_ClaimLease_ExpiredLeaseIsRedelivered() =>
        await WithSqlite(o => { o.ClaimLease = TimeSpan.FromSeconds(1); o.MaxBatchSize = 1; o.MaxConcurrency = 2; },
            AssertLeaseExpiryRedelivers);

    [Test]
    public async Task Sqlite_HealthCheck_ReportsPollingAndThresholds()
    {
        await WithSqlite(o => { }, async bus =>
        {
            var services = new ServiceCollection().AddLogging().AddSingleton<IMessageBus>(bus);
            services.AddHealthChecks().AddMessageBus(configure: o => o
                .Queue<OrderSubmitted>(q => { q.MaxPending = 1; q.MaxOldestReadyAge = TimeSpan.FromMinutes(1); }));
            await using var sp = services.BuildServiceProvider();
            var health = sp.GetRequiredService<HealthCheckService>();

            var healthy = await health.CheckHealthAsync();
            Assert.That(healthy.Status, Is.EqualTo(HealthStatus.Healthy));

            // No subscriber: the messages stay pending and exceed MaxPending.
            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "a" });
            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "b" });
            var degraded = await health.CheckHealthAsync();
            var entry = degraded.Entries["message-bus"];
            Assert.That(entry.Status, Is.EqualTo(HealthStatus.Degraded));
            Assert.That(entry.Description, Does.Contain("2 pending"));
            Assert.That(entry.Data[$"{typeof(OrderSubmitted).FullName}.pending"], Is.EqualTo(2L));

            await bus.DisposeAsync();
            var stopped = await health.CheckHealthAsync();
            Assert.That(stopped.Status, Is.EqualTo(HealthStatus.Unhealthy), "A stopped bus is unhealthy.");
        });
    }

    [Test]
    public async Task Sqlite_TrackQueue_PublishesGauges()
    {
        await WithSqlite(o => { }, async bus =>
        {
            var channel = typeof(OrderSubmitted).FullName!;
            var values  = new ConcurrentDictionary<string, double>();

            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == WorkQueueMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            };
            void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                foreach (var tag in tags)
                    if (tag.Key == "messaging.destination.name" && (string?)tag.Value == channel)
                        values[instrument.Name] = value;
            }
            listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
            listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
            listener.Start();

            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "gauge" });
            await using (WorkQueueMetrics.TrackQueue<OrderSubmitted>(bus, TimeSpan.FromMilliseconds(100)))
            {
                await WaitFor(() =>
                {
                    listener.RecordObservableInstruments();
                    return values.GetValueOrDefault("townsuite.workqueues.queue.pending") == 1;
                });

                Assert.That(values["townsuite.workqueues.queue.pending"], Is.EqualTo(1));
                Assert.That(values["townsuite.workqueues.queue.deadlettered"], Is.Zero);
                Assert.That(values["townsuite.workqueues.queue.oldest_ready_age"], Is.GreaterThanOrEqualTo(0));
            }

            values.Clear();
            listener.RecordObservableInstruments();
            Assert.That(values, Is.Empty, "A disposed tracker no longer reports.");
        });
    }

    private static async Task WithSqlite(Action<SqliteTransportOptions> configure, Func<SqliteMessageBus, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wq-maint-{Guid.NewGuid()}.db");
        var options = new SqliteTransportOptions
        {
            ConnectionString     = $"Data Source={path}",
            ContinuousPolling    = true,
            MaxBatchSize         = 10,
            MaxWaitTime          = TimeSpan.FromMilliseconds(100),
            FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
        };
        configure(options);
        try
        {
            var sp = new ServiceCollection().AddSingleton(options).AddLogging().BuildServiceProvider();
            await new SqliteMigrationHostedService(sp, sp.GetRequiredService<ILogger<SqliteMigrationHostedService>>())
                .StartAsync(CancellationToken.None);

            await using var bus = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
            await test(bus);
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
    public async Task SqlServer_LeaseExpiryAndPurge()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("mssql");
        await wrapper.StartAsync();
        var cs = wrapper.Container.GetConnectionString();

        SqlServerTransportOptions Options(Action<SqlServerTransportOptions>? configure = null)
        {
            var o = new SqlServerTransportOptions
            {
                ConnectionString = cs, Schema = "dbo", ContinuousPolling = true, MaxBatchSize = 10,
                MaxWaitTime = TimeSpan.FromMilliseconds(100), FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
            };
            configure?.Invoke(o);
            return o;
        }
        SqlServerMessageBus Bus(SqlServerTransportOptions o) => new(o, Moq.Mock.Of<ILogger<SqlServerMessageBus>>());
        async Task Clear() { await using var cn = new SqlConnection(cs); await cn.ExecuteAsync("DELETE FROM dbo.workqueue"); }

        // Lease mode holds no row lock while the consumer runs: another connection can update the row.
        var blocked = new BlockingConsumer();
        await using (var bus = Bus(Options(o => o.ClaimLease = TimeSpan.FromSeconds(30))))
        {
            bus.Subscribe(blocked);
            var id = await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "no lock" },
                new PublishOptions());
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await using (var cn = new SqlConnection(cs))
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync("SET LOCK_TIMEOUT 1000");
                var updated = await cn.ExecuteAsync("UPDATE dbo.workqueue SET payload = payload WHERE messageid = @id", new { id });
                Assert.That(updated, Is.EqualTo(1), "The claimed row must not be locked in lease mode.");
                var token = await cn.QuerySingleAsync<Guid?>("SELECT locktoken FROM dbo.workqueue WHERE messageid = @id", new { id });
                Assert.That(token, Is.Not.Null, "The claim is recorded as a lease.");
            }
            blocked.Release.SetResult();
            await WaitFor(async () => (await bus.GetQueueStatistics<OrderSubmitted>()).PendingCount == 0, timeoutMs: 5000);
        }
        await Clear();

        await using (var bus = Bus(Options(o => { o.ClaimLease = TimeSpan.FromSeconds(1); o.MaxBatchSize = 1; o.MaxConcurrency = 2; })))
            await AssertLeaseExpiryRedelivers(bus);
        await Clear();

        await using (var bus = Bus(Options())) await AssertExpiry(bus);
        await Clear();
        await using (var bus = Bus(Options(o => o.ClaimLease = TimeSpan.FromSeconds(30)))) await AssertPurge(bus);
        await Clear();
        await using (var bus = Bus(Options(o => o.MaxRetries = 1))) await AssertPurgeDeadLettered(bus);
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    [Test]
    public async Task Postgres_LeaseExpiryPurgeAndUtc()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("postgres");
        await wrapper.StartAsync();

        // A session time zone far from UTC: timestamps must still be UTC.
        var cs = new NpgsqlConnectionStringBuilder(wrapper.Container.GetConnectionString()) { Timezone = "America/St_Johns" }.ToString();

        SqlTransportOptions Options(Action<SqlTransportOptions>? configure = null)
        {
            var o = new SqlTransportOptions
            {
                ConnectionString = cs, Schema = "public", ContinuousPolling = true, MaxBatchSize = 10,
                MaxWaitTime = TimeSpan.FromMilliseconds(100), FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
            };
            configure?.Invoke(o);
            return o;
        }
        PostgresMessageBus Bus(SqlTransportOptions o) => new(o, Moq.Mock.Of<ILogger<PostgresMessageBus>>());
        async Task Clear() { await using var cn = new NpgsqlConnection(cs); await cn.ExecuteAsync("DELETE FROM public.workqueue"); }

        await using (var bus = Bus(Options()))
        {
            var before = DateTime.UtcNow;
            var id = await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "utc" },
                new PublishOptions { DeliverAfter = DateTimeOffset.UtcNow.AddSeconds(1) });

            await using var cn = new NpgsqlConnection(cs);
            var created = await cn.QuerySingleAsync<DateTime>("SELECT timecreatedutc FROM public.workqueue WHERE messageid = @id", new { id });
            Assert.That(created, Is.EqualTo(before).Within(TimeSpan.FromSeconds(30)),
                "timecreatedutc is UTC even when the session time zone is not.");

            var consumer = new CountingConsumer();
            bus.Subscribe(consumer);
            await Task.Delay(400);
            Assert.That(consumer.Count, Is.Zero, "Scheduled delivery compares in UTC: not due yet.");
            await WaitFor(() => consumer.Count == 1, timeoutMs: 5000);
            Assert.That(consumer.Count, Is.EqualTo(1));
        }
        await Clear();

        await using (var bus = Bus(Options(o => { o.ClaimLease = TimeSpan.FromSeconds(1); o.MaxBatchSize = 1; o.MaxConcurrency = 2; })))
            await AssertLeaseExpiryRedelivers(bus);
        await Clear();

        await using (var bus = Bus(Options())) await AssertExpiry(bus);
        await Clear();
        await using (var bus = Bus(Options(o => o.ClaimLease = TimeSpan.FromSeconds(30)))) await AssertPurge(bus);
        await Clear();
        await using (var bus = Bus(Options(o => o.MaxRetries = 1))) await AssertPurgeDeadLettered(bus);
    }

    // ── Redis ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Redis_ScheduledBackoffTransactionalExpiryAndPurge()
    {
        await using var container = new RedisBuilder().Build();
        await container.StartAsync();
        using var mux = ConnectionMultiplexer.Connect(container.GetConnectionString());
        int prefix = 0;

        RedisOptions Options(Action<RedisOptions>? configure = null)
        {
            var o = new RedisOptions
            {
                KeyPrefix = $"maint{++prefix}", ConsumerGroup = "g", ConsumerName = "c", MaxBatchSize = 10,
                MaxWaitTime = TimeSpan.FromMilliseconds(100), ReclaimIdleTime = TimeSpan.FromSeconds(30),
                FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
            };
            configure?.Invoke(o);
            return o;
        }
        RedisMessageBus Bus(RedisOptions o) => new(mux, o, Moq.Mock.Of<ILogger<RedisMessageBus>>());

        // Scheduled delivery.
        await using (var bus = Bus(Options()))
        {
            var consumer = new CountingConsumer();
            bus.Subscribe(consumer);
            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "later" },
                DateTimeOffset.UtcNow.AddSeconds(1));
            await Task.Delay(400);
            Assert.That(consumer.Count, Is.Zero, "Not delivered before its time.");
            Assert.That((await bus.GetQueueStatistics<OrderSubmitted>()).PendingCount, Is.EqualTo(1), "Scheduled counts as pending.");
            await WaitFor(() => consumer.Count == 1, timeoutMs: 5000);
            Assert.That(consumer.Count, Is.EqualTo(1));
        }

        // Retry backoff: retried after RetryDelay rather than ReclaimIdleTime (30s).
        await using (var bus = Bus(Options(o => { o.MaxRetries = 3; o.RetryDelay = TimeSpan.FromMilliseconds(500); })))
        {
            var consumer = new FailOnceConsumer();
            bus.Subscribe(consumer);
            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "backoff" });
            await WaitFor(() => consumer.Succeeded, timeoutMs: 5000);

            Assert.That(consumer.Succeeded, Is.True, "Retried after the retry delay, not the 30s reclaim time.");
            Assert.That(consumer.Calls, Is.EqualTo(2));
            Assert.That(consumer.Gap, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400)));
        }

        // Transactional publish on a Redis MULTI.
        await using (var bus = Bus(Options()))
        {
            var consumer = new CountingConsumer();
            bus.Subscribe(consumer);

            var abandoned = mux.GetDatabase().CreateTransaction();
            bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "never executed" }, abandoned);

            var tran = mux.GetDatabase().CreateTransaction();
            bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "executed" }, tran);
            Assert.That(await tran.ExecuteAsync(), Is.True);

            await WaitFor(() => consumer.Count >= 1, timeoutMs: 5000);
            await Task.Delay(300);
            Assert.That(consumer.Count, Is.EqualTo(1), "Only the executed transaction's message is delivered.");
        }

        await using (var bus = Bus(Options())) await AssertExpiry(bus);
        await using (var bus = Bus(Options())) await AssertPurge(bus);
        await using (var bus = Bus(Options(o => o.IsRetryable = _ => false))) await AssertPurgeDeadLettered(bus);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
}

internal class SlowFirstCallConsumer(TimeSpan firstCallDelay) : IConsumer<OrderSubmitted>
{
    private int _calls;
    public int Calls => _calls;

    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        if (Interlocked.Increment(ref _calls) == 1)
            await Task.Delay(firstCallDelay);
    }
}

internal class BlockingConsumer : IConsumer<OrderSubmitted>
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        Started.TrySetResult();
        await Release.Task;
    }
}

internal class FailOnceConsumer : IConsumer<OrderSubmitted>
{
    private int _calls;
    private DateTime _firstCall;
    public int Calls => _calls;
    public bool Succeeded { get; private set; }
    public TimeSpan Gap { get; private set; }

    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            _firstCall = DateTime.UtcNow;
            throw new InvalidOperationException("first attempt fails");
        }
        Gap = DateTime.UtcNow - _firstCall;
        Succeeded = true;
        return Task.CompletedTask;
    }
}
