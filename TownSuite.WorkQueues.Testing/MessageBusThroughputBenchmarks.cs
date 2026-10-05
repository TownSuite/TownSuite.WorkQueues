using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;
using TownSuite.WorkQueues.Postgres;
using TownSuite.WorkQueues.SqlServer;

namespace TownSuite.WorkQueues.Testing;

/// <summary>
/// Throughput measurements for the message bus against real PostgreSQL and SQL Server containers.
/// Explicit: run with <c>dotnet test --filter "Category=Benchmark"</c>. Results are written to the
/// test output, one line per configuration.
/// </summary>
[TestFixture, Explicit("Benchmark — run on demand"), Category("Benchmark")]
public class MessageBusThroughputBenchmarks
{
    private const int Messages = 400;
    private static readonly TimeSpan ConsumerWork = TimeSpan.FromMilliseconds(20);

    private static readonly (int Concurrency, int BatchSize, TimeSpan? Lease)[] Configurations =
    {
        (1, 1,  null),
        (4, 1,  null),
        (8, 1,  null),
        (1, 10, null),
        (4, 10, TimeSpan.FromMinutes(1)),
        (8, 10, TimeSpan.FromMinutes(1)),
    };

    [Test]
    public async Task SqlServer_Throughput()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("mssql");
        await wrapper.StartAsync();
        var cs = wrapper.Container.GetConnectionString();

        foreach (var (concurrency, batch, lease) in Configurations)
        {
            await using (var cn = new SqlConnection(cs)) await cn.ExecuteAsync("DELETE FROM dbo.workqueue");
            var options = new SqlServerTransportOptions
            {
                ConnectionString = cs, Schema = "dbo", MaxConcurrency = concurrency, MaxBatchSize = batch,
                ClaimLease = lease, MaxWaitTime = TimeSpan.FromMilliseconds(50)
            };
            await Measure("sqlserver", concurrency, batch, lease,
                () => new SqlServerMessageBus(options, Moq.Mock.Of<ILogger<SqlServerMessageBus>>()));
        }

        await MeasureFaultRedelivery("sqlserver", () => new SqlServerMessageBus(new SqlServerTransportOptions
        {
            ConnectionString = cs, Schema = "dbo", MaxRetries = 1, MaxBatchSize = 50,
            MaxWaitTime = TimeSpan.FromMilliseconds(50), FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
        }, Moq.Mock.Of<ILogger<SqlServerMessageBus>>()), async () =>
        {
            await using var cn = new SqlConnection(cs);
            await cn.ExecuteAsync("DELETE FROM dbo.workqueue");
        });
    }

    [Test]
    public async Task Postgres_Throughput()
    {
        await using var wrapper = await TestContainerWrapper.CreateContainerAsync("postgres");
        await wrapper.StartAsync();
        var cs = wrapper.Container.GetConnectionString();

        foreach (var (concurrency, batch, lease) in Configurations)
        {
            await using (var cn = new NpgsqlConnection(cs)) await cn.ExecuteAsync("DELETE FROM public.workqueue");
            var options = new SqlTransportOptions
            {
                ConnectionString = cs, Schema = "public", MaxConcurrency = concurrency, MaxBatchSize = batch,
                ClaimLease = lease, MaxWaitTime = TimeSpan.FromMilliseconds(50)
            };
            await Measure("postgres", concurrency, batch, lease,
                () => new PostgresMessageBus(options, Moq.Mock.Of<ILogger<PostgresMessageBus>>()));
        }

        await MeasureFaultRedelivery("postgres", () => new PostgresMessageBus(new SqlTransportOptions
        {
            ConnectionString = cs, Schema = "public", MaxRetries = 1, MaxBatchSize = 50,
            MaxWaitTime = TimeSpan.FromMilliseconds(50), FaultRedeliveryDelay = TimeSpan.FromMilliseconds(500)
        }, Moq.Mock.Of<ILogger<PostgresMessageBus>>()), async () =>
        {
            await using var cn = new NpgsqlConnection(cs);
            await cn.ExecuteAsync("DELETE FROM public.workqueue");
        });
    }

    // Publishes the messages, then times how long one bus takes to consume them all.
    private static async Task Measure(string transport, int concurrency, int batch, TimeSpan? lease, Func<IMessageBus> createBus)
    {
        await using (var publisher = createBus())
        {
            var publishTimer = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(0, Messages), new ParallelOptions { MaxDegreeOfParallelism = 8 },
                async (i, ct) => await publisher.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = $"bench {i}" }, ct));
            publishTimer.Stop();
            TestContext.Out.WriteLine($"{transport} publish: {Messages / publishTimer.Elapsed.TotalSeconds:N0} msg/s (8 parallel publishers)");
        }

        var consumer = new WorkSimulatingConsumer(ConsumerWork);
        var timer = Stopwatch.StartNew();
        await using (var bus = createBus())
        {
            bus.Subscribe(consumer);
            while (consumer.Count < Messages && timer.Elapsed < TimeSpan.FromMinutes(3))
                await Task.Delay(20);
            timer.Stop();
        }

        TestContext.Out.WriteLine(
            $"{transport} consume: concurrency={concurrency,2} batch={batch,2} claim={(lease == null ? "transaction" : "lease"),-11} " +
            $"{consumer.Count / timer.Elapsed.TotalSeconds,7:N1} msg/s  ({consumer.Count}/{Messages} in {timer.Elapsed.TotalSeconds:N1}s, " +
            $"{ConsumerWork.TotalMilliseconds:N0} ms consumer)");
        Assert.That(consumer.Count, Is.EqualTo(Messages));
    }

    // Dead-letters a batch with no fault consumer, then times a second bus delivering every fault.
    private static async Task MeasureFaultRedelivery(string transport, Func<IMessageBus> createBus, Func<Task> clear)
    {
        const int faults = 200;
        await clear();

        await using (var deadLetterer = createBus())
        {
            deadLetterer.Subscribe(new AlwaysThrowingConsumer<OrderSubmitted>());
            for (int i = 0; i < faults; i++)
                await deadLetterer.Publish(new OrderSubmitted { OrderId = Guid.NewGuid(), ProductName = $"fault {i}" });
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while ((await deadLetterer.GetQueueStatistics<OrderSubmitted>()).DeadLetteredCount < faults && DateTime.UtcNow < deadline)
                await Task.Delay(100);
        }

        var received = new CountingFaultConsumer();
        var timer = Stopwatch.StartNew();
        await using (var bus = createBus())
        {
            bus.SubscribeFault(received);
            while (received.Count < faults && timer.Elapsed < TimeSpan.FromMinutes(2))
                await Task.Delay(20);
            timer.Stop();
        }

        TestContext.Out.WriteLine($"{transport} fault redelivery: {received.Count}/{faults} faults in {timer.Elapsed.TotalSeconds:N1}s " +
                                  $"({received.Count / timer.Elapsed.TotalSeconds:N0} faults/s, includes the 0.5s redelivery delay)");
        Assert.That(received.Count, Is.EqualTo(faults));
    }

    private sealed class WorkSimulatingConsumer(TimeSpan work) : IConsumer<OrderSubmitted>
    {
        private int _count;
        public int Count => _count;

        public async Task Consume(ConsumeContext<OrderSubmitted> context)
        {
            await Task.Delay(work);
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class CountingFaultConsumer : IConsumer<Fault<OrderSubmitted>>
    {
        private int _count;
        public int Count => _count;

        public Task Consume(ConsumeContext<Fault<OrderSubmitted>> context)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
