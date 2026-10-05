using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
/// Retry backoff, non-retryable exceptions, single-message replay, transactional publish,
/// bounded concurrency, queue statistics, metrics and post-commit fault dispatch.
/// </summary>
[TestFixture]
public class MessageBusReliabilityTests
{
    // ── BatchOptions (no backing store) ───────────────────────────────────────

    [Test]
    public void GetRetryDelay_DefaultMultiplier_IsFixed()
    {
        var options = new BatchOptions { RetryDelay = TimeSpan.FromSeconds(2) };

        Assert.That(options.GetRetryDelay(1), Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(options.GetRetryDelay(5), Is.EqualTo(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void GetRetryDelay_Exponential_GrowsAndIsCapped()
    {
        var options = new BatchOptions
        {
            RetryDelay             = TimeSpan.FromMilliseconds(250),
            RetryBackoffMultiplier = 2.0,
            MaxRetryDelay          = TimeSpan.FromSeconds(1)
        };

        Assert.That(options.GetRetryDelay(1), Is.EqualTo(TimeSpan.FromMilliseconds(250)));
        Assert.That(options.GetRetryDelay(2), Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(options.GetRetryDelay(3), Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(options.GetRetryDelay(50), Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void GetRetryDelay_NoCap_IsBoundedToOneDay()
    {
        var options = new BatchOptions { RetryDelay = TimeSpan.FromMinutes(1), RetryBackoffMultiplier = 10 };

        Assert.That(options.GetRetryDelay(100), Is.EqualTo(TimeSpan.FromDays(1)));
    }

    [Test]
    public void GetRetryDelay_ZeroDelay_IsZero()
    {
        var options = new BatchOptions { RetryBackoffMultiplier = 3 };

        Assert.That(options.GetRetryDelay(4), Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void ShouldRetry_DefaultsToTrue_AndHonoursPredicate()
    {
        var options = new BatchOptions();
        Assert.That(options.ShouldRetry(new InvalidOperationException()), Is.True);

        options.IsRetryable = ex => ex is not ArgumentException;
        Assert.That(options.ShouldRetry(new InvalidOperationException()), Is.True);
        Assert.That(options.ShouldRetry(new ArgumentException()), Is.False);
    }

    [Test]
    public void QueueStatistics_OldestReadyAge_IsRelativeToCapture()
    {
        var captured = DateTimeOffset.UtcNow;
        var stats = new QueueStatistics
        {
            Channel = "c", PendingCount = 1, DeadLetteredCount = 0,
            OldestReadySince = captured.AddSeconds(-30), CapturedAt = captured
        };
        Assert.That(stats.OldestReadyAge, Is.EqualTo(TimeSpan.FromSeconds(30)));

        var empty = new QueueStatistics { Channel = "c", PendingCount = 0, DeadLetteredCount = 0, CapturedAt = captured };
        Assert.That(empty.OldestReadyAge, Is.Null);
    }

    // ── SQLite ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sqlite_ExponentialBackoff_SchedulesGrowingDelays()
    {
        await using var db = await SqliteDb.CreateAsync();
        var options = db.Options(maxRetries: 5);
        options.RetryDelay = TimeSpan.FromMilliseconds(500);
        options.RetryBackoffMultiplier = 4.0;

        var consumer = new AlwaysThrowingConsumer<OrderSubmitted>();
        await using var bus = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        bus.Subscribe(consumer);
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "backoff" });

        await WaitFor(() => consumer.CallCount >= 1);
        var firstFailure = DateTime.UtcNow;
        var first = await db.ScheduledForAsync();

        // Second delivery happens ~500ms later; the next delay is 4 × 500ms = 2s.
        await WaitFor(() => consumer.CallCount >= 2);
        var secondFailure = DateTime.UtcNow;
        await Task.Delay(100);
        var second = await db.ScheduledForAsync();

        Assert.That(first!.Value - firstFailure, Is.EqualTo(TimeSpan.FromMilliseconds(500)).Within(TimeSpan.FromMilliseconds(400)));
        Assert.That(second!.Value - secondFailure, Is.EqualTo(TimeSpan.FromSeconds(2)).Within(TimeSpan.FromMilliseconds(400)));
        Assert.That(consumer.CallCount, Is.EqualTo(2), "Third attempt must wait for the 2s backoff.");
    }

    [Test]
    public async Task Sqlite_NonRetryableException_DeadLettersOnFirstFailure()
    {
        await using var db = await SqliteDb.CreateAsync();
        var options = db.Options(maxRetries: 5);
        options.IsRetryable = ex => ex is not ArgumentException;

        var consumer = new ArgumentThrowingConsumer();
        var faults   = new CapturingFaultConsumer<OrderSubmitted>();
        await using var bus = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        bus.Subscribe(consumer);
        bus.SubscribeFault(faults);
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "permanent" });

        await WaitFor(() => faults.Received != null);
        await Task.Delay(300);

        Assert.That(consumer.CallCount, Is.EqualTo(1), "A non-retryable failure must not be retried.");
        Assert.That(faults.Received!.NonRetryable, Is.True);
        Assert.That(faults.Received.AttemptCount, Is.EqualTo(1));
        Assert.That(faults.Received.ExceptionType, Is.EqualTo(typeof(ArgumentException).FullName));
        Assert.That(faults.Received.MessageId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task Sqlite_ReplaySingleDeadLettered_ReplaysOnlyThatMessage()
    {
        await using var db = await SqliteDb.CreateAsync();
        var faults = new ConcurrentBag<Fault<OrderSubmitted>>();

        var bus = new SqliteMessageBus(db.Options(maxRetries: 1), Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        bus.Subscribe(new AlwaysThrowingConsumer<OrderSubmitted>());
        bus.SubscribeFault(new CollectingFaultConsumer<OrderSubmitted>(faults));
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "a" });
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "b" });

        await WaitFor(() => faults.Count == 2);
        await bus.DisposeAsync();

        var target = faults.Single(f => f.OriginalMessage.ProductName == "a");
        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(target.MessageId), Is.True);
        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(target.MessageId), Is.False,
            "A message that is no longer dead-lettered cannot be replayed again.");
        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(Guid.NewGuid()), Is.False);

        var stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingCount, Is.EqualTo(1));
        Assert.That(stats.DeadLetteredCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Sqlite_TransactionalPublish_OnlyDeliversCommittedMessages()
    {
        await using var db = await SqliteDb.CreateAsync();
        var consumer = new MetadataCapturingConsumer();
        var counter  = new CountingConsumer();
        await using var bus = new SqliteMessageBus(db.Options(), Moq.Mock.Of<ILogger<SqliteMessageBus>>());

        await using (var conn = new SqliteConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var rolledBack = conn.BeginTransaction())
            {
                await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "rolled back" }, conn, rolledBack);
                rolledBack.Rollback();
            }

            Guid committedId;
            await using (var committed = conn.BeginTransaction())
            {
                committedId = await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "committed" }, conn, committed);
                committed.Commit();
            }

            bus.Subscribe(consumer);
            bus.Subscribe(counter);
            await WaitFor(() => counter.Count >= 1);
            await Task.Delay(300);

            Assert.That(counter.Count, Is.EqualTo(1), "Only the committed message should be delivered.");
            Assert.That(consumer.CapturedMessageId, Is.EqualTo(committedId));
        }
    }

    [Test]
    public async Task Sqlite_MaxConcurrency_ProcessesMessagesInParallel()
    {
        await using var db = await SqliteDb.CreateAsync();
        var options = db.Options();
        options.MaxBatchSize   = 1;
        options.MaxConcurrency = 4;

        var consumer = new SlowConsumer(TimeSpan.FromMilliseconds(400));
        await using var bus = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        Assert.That(bus.IsPolling, Is.True);

        for (int i = 0; i < 8; i++)
            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = $"parallel {i}" });
        bus.Subscribe(consumer);

        await WaitFor(() => consumer.Completed >= 8, timeoutMs: 8000);

        Assert.That(consumer.Completed, Is.EqualTo(8));
        Assert.That(consumer.MaxInFlight, Is.GreaterThan(1), "Several loops should process at the same time.");
        Assert.That(consumer.MaxInFlight, Is.LessThanOrEqualTo(4), "No more than MaxConcurrency × MaxBatchSize in flight.");
    }

    [Test]
    public async Task Sqlite_GetQueueStatistics_ReportsPendingDeadLetteredAndOldest()
    {
        await using var db = await SqliteDb.CreateAsync();
        // No subscriber: messages stay pending.
        await using var bus = new SqliteMessageBus(db.Options(), Moq.Mock.Of<ILogger<SqliteMessageBus>>());

        var empty = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(empty.PendingCount, Is.Zero);
        Assert.That(empty.OldestReadySince, Is.Null);

        var beforePublish = DateTimeOffset.UtcNow.AddSeconds(-1);
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "ready" });
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "later" },
            DateTimeOffset.UtcNow.AddHours(1));
        await db.InsertDeadLetteredAsync(typeof(OrderSubmitted).FullName!);

        var stats = await bus.GetQueueStatistics<OrderSubmitted>();

        Assert.That(stats.Channel, Is.EqualTo(typeof(OrderSubmitted).FullName));
        Assert.That(stats.PendingCount, Is.EqualTo(2), "Scheduled messages count as pending.");
        Assert.That(stats.DeadLetteredCount, Is.EqualTo(1));
        Assert.That(stats.OldestReadySince, Is.GreaterThan(beforePublish));
        Assert.That(stats.OldestReadyAge, Is.LessThan(TimeSpan.FromMinutes(1)),
            "The future-scheduled message must not count towards the oldest waiting age.");
    }

    [Test]
    public async Task Sqlite_Metrics_RecordPublishedProcessedRetriedAndDeadLettered()
    {
        await using var db = await SqliteDb.CreateAsync();
        var channel = typeof(MetricsProbe).FullName!;
        var counts  = new ConcurrentDictionary<string, long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkQueueMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "messaging.destination.name" && (string?)tag.Value == channel)
                    counts.AddOrUpdate(instrument.Name, value, (_, v) => v + value);
        });
        listener.Start();

        await using var bus = new SqliteMessageBus(db.Options(maxRetries: 2), Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        bus.Subscribe(new MetricsProbeConsumer());
        await bus.Publish(new MetricsProbe { Fail = false });
        await bus.Publish(new MetricsProbe { Fail = true });

        await WaitFor(() => counts.GetValueOrDefault("townsuite.workqueues.messages.deadlettered") >= 1);

        Assert.That(counts.GetValueOrDefault("townsuite.workqueues.messages.published"), Is.EqualTo(2));
        Assert.That(counts.GetValueOrDefault("townsuite.workqueues.messages.processed"), Is.EqualTo(1));
        Assert.That(counts.GetValueOrDefault("townsuite.workqueues.messages.retried"), Is.EqualTo(1));
        Assert.That(counts.GetValueOrDefault("townsuite.workqueues.messages.deadlettered"), Is.EqualTo(1));
    }

    [Test]
    public async Task Sqlite_FaultConsumerThrows_FaultIsRedelivered()
    {
        await using var db = await SqliteDb.CreateAsync();
        var options = db.Options(maxRetries: 1);
        options.FaultRedeliveryDelay = TimeSpan.FromMilliseconds(300);

        var faults = new FlakyFaultConsumer<OrderSubmitted>(failures: 1);
        await using var bus = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        bus.Subscribe(new AlwaysThrowingConsumer<OrderSubmitted>());
        bus.SubscribeFault(faults);
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "flaky fault" });

        await WaitFor(() => faults.Delivered.Count >= 1);
        await WaitFor(async () => (await bus.GetQueueStatistics<OrderSubmitted>()).PendingFaultCount == 0);

        Assert.That(faults.Attempts, Is.EqualTo(2), "The fault should be delivered again after the consumer threw.");
        Assert.That(faults.Delivered.Single().IsRedelivery, Is.True);
        Assert.That(faults.Delivered.Single().ExceptionType, Is.EqualTo(typeof(InvalidOperationException).FullName));
        Assert.That(faults.Delivered.Single().StackTrace, Is.Not.Null, "The stored error keeps the stack trace.");

        var listed = (await bus.GetDeadLettered<OrderSubmitted>()).Single();
        Assert.That(listed.FaultDelivered, Is.True);
    }

    [Test]
    public async Task Sqlite_FaultNeverDelivered_IsDeliveredByAnotherBus()
    {
        await using var db = await SqliteDb.CreateAsync();
        var options = db.Options(maxRetries: 1);
        options.FaultRedeliveryDelay = TimeSpan.FromMilliseconds(300);

        // Bus A dead-letters the message but has no fault consumer — like a worker that
        // stopped before its fault consumer ran.
        var busA = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        busA.Subscribe(new AlwaysThrowingConsumer<OrderSubmitted>());
        await busA.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "orphaned fault" });
        await WaitFor(async () => (await busA.GetQueueStatistics<OrderSubmitted>()).DeadLetteredCount == 1);
        await busA.DisposeAsync();

        var stats = await busA.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingFaultCount, Is.EqualTo(1));

        // Bus B only subscribes a fault consumer.
        var faults = new CapturingFaultConsumer<OrderSubmitted>();
        await using var busB = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        busB.SubscribeFault(faults);

        await WaitFor(() => faults.Received != null);

        Assert.That(faults.Received, Is.Not.Null);
        Assert.That(faults.Received!.IsRedelivery, Is.True);
        Assert.That(faults.Received.OriginalMessage.ProductName, Is.EqualTo("orphaned fault"));
        Assert.That(faults.Received.AttemptCount, Is.EqualTo(1));
        await WaitFor(async () => (await busB.GetQueueStatistics<OrderSubmitted>()).PendingFaultCount == 0);
        Assert.That((await busB.GetQueueStatistics<OrderSubmitted>()).PendingFaultCount, Is.Zero);
    }

    [Test]
    public async Task Sqlite_GetDeadLettered_ListsNewestFirstWithErrorsAndPages()
    {
        await using var db = await SqliteDb.CreateAsync();
        var options = db.Options(maxRetries: 5);
        options.IsRetryable = ex => ex is not ArgumentException;

        await using var bus = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
        bus.Subscribe(new ArgumentThrowingConsumer());
        for (int i = 0; i < 3; i++)
        {
            await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = $"listed {i}" });
            await WaitFor(async () => (await bus.GetQueueStatistics<OrderSubmitted>()).DeadLetteredCount == i + 1);
        }

        var all = await bus.GetDeadLettered<OrderSubmitted>();
        Assert.That(all.Select(m => m.Message!.ProductName), Is.EqualTo(new[] { "listed 2", "listed 1", "listed 0" }));
        Assert.That(all.All(m => m.ExceptionType == typeof(ArgumentException).FullName), Is.True);
        Assert.That(all.All(m => m.ExceptionMessage == "Simulated permanent failure"), Is.True);
        Assert.That(all.All(m => m.NonRetryable && m.AttemptCount == 1 && m.MessageId != Guid.Empty), Is.True);
        Assert.That(all.All(m => m.FailedAt >= m.SentTime), Is.True);

        var page = await bus.GetDeadLettered<OrderSubmitted>(skip: 1, take: 1);
        Assert.That(page.Single().Message!.ProductName, Is.EqualTo("listed 1"));

        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(page.Single().MessageId), Is.True);
    }

    [Test]
    public async Task Sqlite_Migration_MarksExistingDeadLettersAsNotified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wq-upgrade-{Guid.NewGuid()}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            // A database created by an earlier version, holding one old dead-letter.
            await using (var conn = new SqliteConnection(connectionString))
            {
                await conn.ExecuteAsync("""
                    CREATE TABLE workqueue (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, messageid TEXT NOT NULL,
                        timecreatedutc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                        channel TEXT NOT NULL, payload TEXT NOT NULL, timeprocessedutc TEXT NULL,
                        failedat TEXT NULL, retrycount INTEGER NOT NULL DEFAULT 0, scheduledfor TEXT NULL,
                        lockeduntil TEXT NULL, locktoken TEXT NULL);
                    INSERT INTO workqueue(channel, payload, messageid, failedat, retrycount)
                    VALUES(@channel, '{}', @id, '2026-01-01T00:00:00.000Z', 3);
                    """, new { channel = typeof(OrderSubmitted).FullName, id = Guid.NewGuid().ToString() });
            }

            var options = new SqliteTransportOptions
            {
                ConnectionString = connectionString, ContinuousPolling = true,
                MaxWaitTime = TimeSpan.FromMilliseconds(100), FaultRedeliveryDelay = TimeSpan.FromMilliseconds(100)
            };
            var sp = new ServiceCollection().AddSingleton(options).AddLogging().BuildServiceProvider();
            await new SqliteMigrationHostedService(sp, sp.GetRequiredService<ILogger<SqliteMigrationHostedService>>())
                .StartAsync(CancellationToken.None);

            var faults = new CapturingFaultConsumer<OrderSubmitted>();
            await using var bus = new SqliteMessageBus(options, Moq.Mock.Of<ILogger<SqliteMessageBus>>());
            bus.SubscribeFault(faults);
            await Task.Delay(800);

            Assert.That(faults.Received, Is.Null, "Dead-letters from before the upgrade must not produce faults.");
            Assert.That((await bus.GetQueueStatistics<OrderSubmitted>()).PendingFaultCount, Is.Zero);
            Assert.That((await bus.GetDeadLettered<OrderSubmitted>()).Single().FaultDelivered, Is.True);
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
    public async Task SqlServer_ReliabilityFeatures()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("mssql");
        await wrapper.StartAsync();
        var connectionString = wrapper.Container.GetConnectionString();

        var options = new SqlServerTransportOptions
        {
            ConnectionString  = connectionString,
            Schema            = "dbo",
            ContinuousPolling = true,
            MaxBatchSize      = 1,
            MaxConcurrency    = 2,
            MaxWaitTime       = TimeSpan.FromMilliseconds(100),
            MaxRetries        = 5,
            IsRetryable       = ex => ex is not ArgumentException
        };

        // Fault consumer checks the dead-letter is visible to another connection, which is only
        // true once the claim transaction has committed (it would block or read NULL before).
        var faults = new ConcurrentBag<(Fault<OrderSubmitted> Fault, DateTime? FailedAt)>();
        var bus = new SqlServerMessageBus(options, Moq.Mock.Of<ILogger<SqlServerMessageBus>>());
        bus.Subscribe(new ArgumentThrowingConsumer());
        bus.SubscribeFault(new DelegateFaultConsumer<OrderSubmitted>(async fault =>
        {
            await using var cn = new SqlConnection(connectionString);
            await cn.OpenAsync();
            await cn.ExecuteAsync("SET LOCK_TIMEOUT 2000");
            var failedAt = await cn.QuerySingleAsync<DateTime?>(
                "SELECT failedat FROM dbo.workqueue WHERE messageid = @id", new { id = fault.MessageId });
            faults.Add((fault, failedAt));
        }));

        Guid committedId;
        await using (var cn = new SqlConnection(connectionString))
        {
            await cn.OpenAsync();
            await using (var rolledBack = cn.BeginTransaction())
            {
                await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "rolled back" }, cn, rolledBack);
                rolledBack.Rollback();
            }
            await using var committed = cn.BeginTransaction();
            committedId = await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "committed" }, cn, committed);
            committed.Commit();
        }

        await WaitFor(() => faults.Count >= 1, timeoutMs: 10000);
        await Task.Delay(500);
        await bus.DisposeAsync();

        Assert.That(faults.Count, Is.EqualTo(1), "Only the committed message should be delivered.");
        var (receivedFault, failedAtSeenByFaultConsumer) = faults.Single();
        Assert.That(receivedFault.MessageId, Is.EqualTo(committedId));
        Assert.That(receivedFault.NonRetryable, Is.True);
        Assert.That(receivedFault.AttemptCount, Is.EqualTo(1));
        Assert.That(failedAtSeenByFaultConsumer, Is.Not.Null, "Fault consumer must run after the commit.");

        var stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingCount, Is.Zero);
        Assert.That(stats.DeadLetteredCount, Is.EqualTo(1));

        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(committedId), Is.True);
        stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingCount, Is.EqualTo(1));
        Assert.That(stats.DeadLetteredCount, Is.Zero);
        Assert.That(stats.OldestReadyAge, Is.Not.Null);
    }

    [Test]
    public async Task SqlServer_ExponentialBackoff_SchedulesGrowingDelays()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("mssql");
        await wrapper.StartAsync();

        var options = new SqlServerTransportOptions
        {
            ConnectionString       = wrapper.Container.GetConnectionString(),
            Schema                 = "dbo",
            ContinuousPolling      = true,
            MaxWaitTime            = TimeSpan.FromMilliseconds(100),
            MaxRetries             = 5,
            RetryDelay             = TimeSpan.FromMilliseconds(500),
            RetryBackoffMultiplier = 4.0
        };

        var consumer = new AlwaysThrowingConsumer<OrderSubmitted>();
        await using var bus = new SqlServerMessageBus(options, Moq.Mock.Of<ILogger<SqlServerMessageBus>>());
        bus.Subscribe(consumer);
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "backoff" });

        await WaitFor(() => consumer.CallCount >= 2, timeoutMs: 6000);
        await Task.Delay(1000);

        Assert.That(consumer.CallCount, Is.EqualTo(2), "Third attempt must wait for the 2s backoff.");
        await WaitFor(() => consumer.CallCount >= 3, timeoutMs: 4000);
        Assert.That(consumer.CallCount, Is.EqualTo(3));
    }

    [Test]
    public async Task SqlServer_FaultRedeliveryAndListing()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("mssql");
        await wrapper.StartAsync();

        SqlServerTransportOptions Options() => new()
        {
            ConnectionString     = wrapper.Container.GetConnectionString(),
            Schema               = "dbo",
            ContinuousPolling    = true,
            MaxWaitTime          = TimeSpan.FromMilliseconds(100),
            MaxRetries           = 1,
            FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
        };

        await AssertFaultRedeliveryAndListing(
            () => new SqlServerMessageBus(Options(), Moq.Mock.Of<ILogger<SqlServerMessageBus>>()),
            bus => ((SqlServerMessageBus)bus).Subscribe(new AlwaysThrowingConsumer<OrderSubmitted>()));
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    [Test]
    public async Task Postgres_FaultRedeliveryAndListing()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("postgres");
        await wrapper.StartAsync();

        SqlTransportOptions Options() => new()
        {
            ConnectionString     = wrapper.Container.GetConnectionString(),
            Schema               = "public",
            ContinuousPolling    = true,
            MaxWaitTime          = TimeSpan.FromMilliseconds(100),
            MaxRetries           = 1,
            FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
        };

        await AssertFaultRedeliveryAndListing(
            () => new PostgresMessageBus(Options(), Moq.Mock.Of<ILogger<PostgresMessageBus>>()),
            bus => ((PostgresMessageBus)bus).Subscribe(new AlwaysThrowingConsumer<OrderSubmitted>()));
    }

    [Test]
    public async Task Postgres_ReliabilityFeatures()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("postgres");
        await wrapper.StartAsync();
        var connectionString = wrapper.Container.GetConnectionString();

        var options = new SqlTransportOptions
        {
            ConnectionString  = connectionString,
            Schema            = "public",
            ContinuousPolling = true,
            MaxBatchSize      = 1,
            MaxConcurrency    = 2,
            MaxWaitTime       = TimeSpan.FromMilliseconds(100),
            MaxRetries        = 5,
            IsRetryable       = ex => ex is not ArgumentException
        };

        var faults = new ConcurrentBag<(Fault<OrderSubmitted> Fault, DateTime? FailedAt)>();
        var bus = new PostgresMessageBus(options, Moq.Mock.Of<ILogger<PostgresMessageBus>>());
        bus.Subscribe(new ArgumentThrowingConsumer());
        bus.SubscribeFault(new DelegateFaultConsumer<OrderSubmitted>(async fault =>
        {
            await using var cn = new NpgsqlConnection(connectionString);
            var failedAt = await cn.QuerySingleAsync<DateTime?>(
                "SELECT failedat FROM public.workqueue WHERE messageid = @id", new { id = fault.MessageId });
            faults.Add((fault, failedAt));
        }));

        Guid committedId;
        await using (var cn = new NpgsqlConnection(connectionString))
        {
            await cn.OpenAsync();
            await using (var rolledBack = await cn.BeginTransactionAsync())
            {
                await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "rolled back" }, cn, rolledBack);
                await rolledBack.RollbackAsync();
            }
            await using var committed = await cn.BeginTransactionAsync();
            committedId = await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "committed" }, cn, committed);
            await committed.CommitAsync();
        }

        await WaitFor(() => faults.Count >= 1, timeoutMs: 10000);
        await Task.Delay(500);
        await bus.DisposeAsync();

        Assert.That(faults.Count, Is.EqualTo(1), "Only the committed message should be delivered.");
        var (receivedFault, failedAtSeenByFaultConsumer) = faults.Single();
        Assert.That(receivedFault.MessageId, Is.EqualTo(committedId));
        Assert.That(receivedFault.NonRetryable, Is.True);
        Assert.That(failedAtSeenByFaultConsumer, Is.Not.Null, "Fault consumer must run after the commit.");

        var stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingCount, Is.Zero);
        Assert.That(stats.DeadLetteredCount, Is.EqualTo(1));

        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(committedId), Is.True);
        stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingCount, Is.EqualTo(1));
        Assert.That(stats.DeadLetteredCount, Is.Zero);
        Assert.That(stats.OldestReadyAge, Is.Not.Null);
    }

    // ── Redis ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Redis_NonRetryable_ReplaySingle_AndStatistics()
    {
        await using var container = new RedisBuilder().Build();
        await container.StartAsync();
        using var mux = ConnectionMultiplexer.Connect(container.GetConnectionString());

        var options = new RedisOptions
        {
            KeyPrefix       = "reliability",
            ConsumerGroup   = "test-group",
            ConsumerName    = "test-consumer",
            MaxBatchSize    = 10,
            MaxConcurrency  = 2,
            MaxWaitTime     = TimeSpan.FromMilliseconds(100),
            MaxRetries      = 5,
            ReclaimIdleTime = TimeSpan.FromSeconds(30),
            IsRetryable     = ex => ex is not ArgumentException
        };

        var faults   = new ConcurrentBag<Fault<OrderSubmitted>>();
        var consumer = new ArgumentThrowingConsumer();
        var bus = new RedisMessageBus(mux, options, Moq.Mock.Of<ILogger<RedisMessageBus>>());
        bus.Subscribe(consumer);
        bus.SubscribeFault(new CollectingFaultConsumer<OrderSubmitted>(faults));

        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "a" });
        await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "b" });

        // ReclaimIdleTime is 30s, so dead-lettering within the wait proves it skipped the retry path.
        await WaitFor(() => faults.Count >= 2, timeoutMs: 6000);
        await bus.DisposeAsync();

        Assert.That(faults.Count, Is.EqualTo(2));
        Assert.That(consumer.CallCount, Is.EqualTo(2));
        Assert.That(faults.All(f => f.NonRetryable && f.MessageId != Guid.Empty), Is.True);

        var stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingCount, Is.Zero);
        Assert.That(stats.DeadLetteredCount, Is.EqualTo(2));

        var target = faults.Single(f => f.OriginalMessage.ProductName == "a");
        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(target.MessageId), Is.True);
        Assert.That(await bus.ReplayDeadLettered<OrderSubmitted>(target.MessageId), Is.False);

        stats = await bus.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.PendingCount, Is.EqualTo(1));
        Assert.That(stats.DeadLetteredCount, Is.EqualTo(1));
        Assert.That(stats.OldestReadySince, Is.Not.Null);
    }

    [Test]
    public async Task Redis_FaultRedeliveryAndListing()
    {
        await using var container = new RedisBuilder().Build();
        await container.StartAsync();
        using var mux = ConnectionMultiplexer.Connect(container.GetConnectionString());

        RedisOptions Options() => new()
        {
            KeyPrefix            = "faultredelivery",
            ConsumerGroup        = "test-group",
            ConsumerName         = "test-consumer",
            MaxWaitTime          = TimeSpan.FromMilliseconds(100),
            MaxRetries           = 5,
            ReclaimIdleTime      = TimeSpan.FromSeconds(30),
            IsRetryable          = _ => false,
            FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
        };

        await AssertFaultRedeliveryAndListing(
            () => new RedisMessageBus(mux, Options(), Moq.Mock.Of<ILogger<RedisMessageBus>>()),
            bus => ((RedisMessageBus)bus).Subscribe(new AlwaysThrowingConsumer<OrderSubmitted>()));
    }

    // Bus A dead-letters with no fault consumer (as if it stopped before the fault ran). Bus B,
    // which only has a fault consumer that throws once, must still receive the fault.
    private static async Task AssertFaultRedeliveryAndListing(Func<IMessageBus> createBus, Action<IMessageBus> subscribeThrowing)
    {
        var busA = createBus();
        subscribeThrowing(busA);
        await busA.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "first" });
        await busA.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = "second" });
        await WaitFor(async () => (await busA.GetQueueStatistics<OrderSubmitted>()).DeadLetteredCount == 2, timeoutMs: 10000);
        await busA.DisposeAsync();

        var stats = await busA.GetQueueStatistics<OrderSubmitted>();
        Assert.That(stats.DeadLetteredCount, Is.EqualTo(2));
        Assert.That(stats.PendingFaultCount, Is.EqualTo(2));

        var listed = await busA.GetDeadLettered<OrderSubmitted>();
        Assert.That(listed, Has.Count.EqualTo(2));
        Assert.That(listed.All(m => !m.FaultDelivered && m.MessageId != Guid.Empty && m.ExceptionType != null), Is.True);
        Assert.That(listed.Select(m => m.Message!.ProductName), Is.EquivalentTo(new[] { "first", "second" }));
        Assert.That(await busA.GetDeadLettered<OrderSubmitted>(skip: 1, take: 5), Has.Count.EqualTo(1));

        var faults = new FlakyFaultConsumer<OrderSubmitted>(failures: 1);
        await using var busB = createBus();
        busB.SubscribeFault(faults);

        await WaitFor(() => faults.Delivered.Count >= 2, timeoutMs: 15000);
        await WaitFor(async () => (await busB.GetQueueStatistics<OrderSubmitted>()).PendingFaultCount == 0, timeoutMs: 5000);

        Assert.That(faults.Delivered, Has.Count.EqualTo(2), "Each fault should be delivered once it succeeds.");
        Assert.That(faults.Delivered.All(f => f.IsRedelivery), Is.True);
        Assert.That(faults.Attempts, Is.EqualTo(3), "One fault consumer failure, then two deliveries.");
        Assert.That((await busB.GetQueueStatistics<OrderSubmitted>()).PendingFaultCount, Is.Zero);
        Assert.That((await busB.GetDeadLettered<OrderSubmitted>()).All(m => m.FaultDelivered), Is.True);
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

    private sealed class SqliteDb : IAsyncDisposable
    {
        private readonly string _path;
        public string ConnectionString { get; }

        private SqliteDb(string path)
        {
            _path = path;
            ConnectionString = $"Data Source={path}";
        }

        public static async Task<SqliteDb> CreateAsync()
        {
            var db = new SqliteDb(Path.Combine(Path.GetTempPath(), $"wq-reliability-{Guid.NewGuid()}.db"));
            var sp = new ServiceCollection().AddSingleton(db.Options()).AddLogging().BuildServiceProvider();
            await new SqliteMigrationHostedService(sp, sp.GetRequiredService<ILogger<SqliteMigrationHostedService>>())
                .StartAsync(CancellationToken.None);
            return db;
        }

        public SqliteTransportOptions Options(int maxRetries = 3) => new()
        {
            ConnectionString  = ConnectionString,
            ContinuousPolling = true,
            MaxBatchSize      = 10,
            MaxWaitTime       = TimeSpan.FromMilliseconds(100),
            MaxRetries        = maxRetries
        };

        public async Task<DateTime?> ScheduledForAsync()
        {
            await using var conn = new SqliteConnection(ConnectionString);
            var value = await conn.QuerySingleAsync<string?>("SELECT scheduledfor FROM workqueue");
            return value == null
                ? null
                : DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        }

        public async Task InsertDeadLetteredAsync(string channel)
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.ExecuteAsync(
                "INSERT INTO workqueue(channel, payload, messageid, failedat, retrycount) VALUES(@channel, '{}', @id, @now, 3)",
                new { channel, id = Guid.NewGuid().ToString(), now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            return ValueTask.CompletedTask;
        }
    }
}

public class MetricsProbe
{
    public bool Fail { get; set; }
}

internal class MetricsProbeConsumer : IConsumer<MetricsProbe>
{
    public Task Consume(ConsumeContext<MetricsProbe> context) =>
        context.Message.Fail ? throw new InvalidOperationException("probe failure") : Task.CompletedTask;
}

internal class ArgumentThrowingConsumer : IConsumer<OrderSubmitted>
{
    private int _callCount;
    public int CallCount => _callCount;

    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        Interlocked.Increment(ref _callCount);
        throw new ArgumentException("Simulated permanent failure");
    }
}

internal class CountingConsumer : IConsumer<OrderSubmitted>
{
    private int _count;
    public int Count => _count;

    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }
}

internal class SlowConsumer(TimeSpan delay) : IConsumer<OrderSubmitted>
{
    private int _inFlight;
    private int _maxInFlight;
    private int _completed;
    public int MaxInFlight => _maxInFlight;
    public int Completed => _completed;

    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = _maxInFlight) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        await Task.Delay(delay);
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _completed);
    }
}

internal class CollectingFaultConsumer<T>(ConcurrentBag<Fault<T>> faults) : IConsumer<Fault<T>>
{
    public Task Consume(ConsumeContext<Fault<T>> context)
    {
        faults.Add(context.Message);
        return Task.CompletedTask;
    }
}

internal class DelegateFaultConsumer<T>(Func<Fault<T>, Task> onFault) : IConsumer<Fault<T>>
{
    public Task Consume(ConsumeContext<Fault<T>> context) => onFault(context.Message);
}

internal class FlakyFaultConsumer<T>(int failures) : IConsumer<Fault<T>>
{
    private int _attempts;
    public int Attempts => _attempts;
    public ConcurrentBag<Fault<T>> Delivered { get; } = new();

    public Task Consume(ConsumeContext<Fault<T>> context)
    {
        if (Interlocked.Increment(ref _attempts) <= failures)
            throw new InvalidOperationException("Simulated fault consumer failure");
        Delivered.Add(context.Message);
        return Task.CompletedTask;
    }
}
