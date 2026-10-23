using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using TownSuite.WorkQueues.Server;
using TownSuite.WorkQueues.Server.Stores;

namespace TownSuite.WorkQueues.Server.Testing;

/// <summary>
/// The fan-out, lease, retry and expiry behaviour every <see cref="ITopicStore"/> must have,
/// run against each backend.
/// </summary>
public abstract class TopicStoreContractTests
{
    // Each test gets its own topic, so backends need no clearing between tests.
    private string _topic = "";

    protected abstract ITopicStore CreateStore(TopicServerOptions options);

    [SetUp]
    public void NewTopic() => _topic = $"t{Guid.NewGuid():N}";

    private ITopicStore Store(Action<TopicServerOptions>? configure = null)
    {
        var options = new TopicServerOptions
        {
            ClaimLease = TimeSpan.FromSeconds(30), MaxRetries = 5, RetryDelay = TimeSpan.FromMilliseconds(300),
            SubscriptionIdleTimeout = TimeSpan.FromMinutes(5)
        };
        configure?.Invoke(options);
        return CreateStore(options);
    }

    private static TopicMessage Message(string payload = "{\"n\":1}", string? trace = null) => new(Guid.NewGuid(), payload, trace);

    [Test]
    public async Task Publish_EachActiveSubscriptionGetsItsOwnCopy()
    {
        var store = Store();
        Assert.That(await store.PublishAsync(_topic, Message(), default), Is.Zero, "Nobody subscribed: dropped.");

        await store.UpsertSubscriptionAsync(_topic, "a", default);
        await store.UpsertSubscriptionAsync(_topic, "b", default);
        var message = Message("{\"hello\":\"world\"}", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        Assert.That(await store.PublishAsync(_topic, message, default), Is.EqualTo(2));

        foreach (var sub in new[] { "a", "b" })
        {
            var claimed = await store.ClaimAsync(_topic, sub, 10, default);
            Assert.That(claimed, Has.Count.EqualTo(1), $"{sub} gets one copy.");
            Assert.That(claimed[0].MessageId, Is.EqualTo(message.MessageId));
            Assert.That(claimed[0].Payload, Does.Contain("world"));
            Assert.That(claimed[0].Attempt, Is.EqualTo(1));
            Assert.That(claimed[0].TraceContext, Is.EqualTo(message.TraceContext));
            Assert.That(await store.AckAsync(_topic, sub, claimed[0].Receipt, default), Is.EqualTo(ReceiptOutcome.Done));
        }
        Assert.That(await store.ClaimAsync(_topic, "a", 10, default), Is.Empty, "Acknowledged copies are not delivered again.");
    }

    [Test]
    public async Task Subscription_ReceivesOnlyMessagesPublishedAfterIt()
    {
        var store = Store();
        await store.UpsertSubscriptionAsync(_topic, "early", default);
        await store.PublishAsync(_topic, Message(), default);
        await store.UpsertSubscriptionAsync(_topic, "late", default);

        Assert.That(await store.ClaimAsync(_topic, "late", 10, default), Is.Empty);
        Assert.That(await store.ClaimAsync(_topic, "early", 10, default), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ExpiredSubscription_StopsReceivingCopies()
    {
        var store = Store(o => o.SubscriptionIdleTimeout = TimeSpan.FromSeconds(1));
        await store.UpsertSubscriptionAsync(_topic, "idle", default);
        await store.UpsertSubscriptionAsync(_topic, "active", default);

        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(500);
            Assert.That(await store.TouchSubscriptionAsync(_topic, "active", default), Is.True, "Polling keeps it alive.");
        }

        Assert.That(await store.PublishAsync(_topic, Message(), default), Is.EqualTo(1), "Only the active subscription gets a copy.");
        Assert.That(await store.TouchSubscriptionAsync(_topic, "idle", default), Is.False);
        Assert.That(await store.GetStatsAsync(_topic, "idle", default), Is.Null);
        Assert.That(await store.ClaimAsync(_topic, "active", 10, default), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Unacknowledged_IsRedeliveredWhenTheLeaseEnds()
    {
        var store = Store(o => o.ClaimLease = TimeSpan.FromSeconds(1));
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        var message = Message();
        await store.PublishAsync(_topic, message, default);

        var first = (await store.ClaimAsync(_topic, "s", 10, default)).Single();
        Assert.That(await store.ClaimAsync(_topic, "s", 10, default), Is.Empty, "Held by the lease.");

        await Task.Delay(1300);
        var second = (await store.ClaimAsync(_topic, "s", 10, default)).Single();
        Assert.That(second.MessageId, Is.EqualTo(message.MessageId));
        Assert.That(second.Attempt, Is.EqualTo(2));

        Assert.That(await store.AckAsync(_topic, "s", first.Receipt, default), Is.EqualTo(ReceiptOutcome.LeaseLost),
            "The first delivery's receipt is no longer valid.");
        Assert.That(await store.AckAsync(_topic, "s", second.Receipt, default), Is.EqualTo(ReceiptOutcome.Done));
    }

    [Test]
    public async Task Unacknowledged_IsDeadLetteredAfterMaxRetries()
    {
        var store = Store(o => { o.ClaimLease = TimeSpan.FromMilliseconds(500); o.MaxRetries = 2; });
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        await store.PublishAsync(_topic, Message(), default);

        Assert.That((await store.ClaimAsync(_topic, "s", 10, default)).Single().Attempt, Is.EqualTo(1));
        await Task.Delay(800);
        Assert.That((await store.ClaimAsync(_topic, "s", 10, default)).Single().Attempt, Is.EqualTo(2));
        await Task.Delay(800);
        Assert.That(await store.ClaimAsync(_topic, "s", 10, default), Is.Empty, "Dead-lettered, not delivered a third time.");

        var stats = await store.GetStatsAsync(_topic, "s", default);
        Assert.That(stats!.DeadLettered, Is.EqualTo(1));
        Assert.That(stats.Pending, Is.Zero);
    }

    [Test]
    public async Task Nack_RetriesAfterTheRetryDelay()
    {
        var store = Store();
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        await store.PublishAsync(_topic, Message(), default);

        var first = (await store.ClaimAsync(_topic, "s", 10, default)).Single();
        Assert.That(await store.NackAsync(_topic, "s", first.Receipt, "boom", default), Is.EqualTo(ReceiptOutcome.Done));
        Assert.That(await store.ClaimAsync(_topic, "s", 10, default), Is.Empty, "Held for the retry delay.");

        await Task.Delay(600);
        var second = (await store.ClaimAsync(_topic, "s", 10, default)).Single();
        Assert.That(second.Attempt, Is.EqualTo(2), "A nack counts as an attempt.");
        Assert.That(await store.NackAsync(_topic, "s", first.Receipt, null, default), Is.EqualTo(ReceiptOutcome.LeaseLost));
    }

    [Test]
    public async Task Receipts_AreValidatedAndSingleUse()
    {
        var store = Store();
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        await store.PublishAsync(_topic, Message(), default);
        var claimed = (await store.ClaimAsync(_topic, "s", 10, default)).Single();

        Assert.That(await store.AckAsync(_topic, "s", "not-a-receipt", default), Is.EqualTo(ReceiptOutcome.InvalidReceipt));
        Assert.That(await store.AckAsync(_topic, "s", claimed.Receipt, default), Is.EqualTo(ReceiptOutcome.Done));
        Assert.That(await store.AckAsync(_topic, "s", claimed.Receipt, default), Is.EqualTo(ReceiptOutcome.LeaseLost));
        Assert.That((await store.GetStatsAsync(_topic, "s", default))!.Pending, Is.Zero);
    }

    [Test]
    public async Task SharedSubscription_SplitsCopiesInPublishOrder()
    {
        var store = Store();
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        var published = new List<Guid>();
        for (int i = 0; i < 4; i++)
        {
            var m = Message($"{{\"n\":{i}}}");
            published.Add(m.MessageId);
            await store.PublishAsync(_topic, m, default);
        }

        var a = await store.ClaimAsync(_topic, "s", 2, default);
        var b = await store.ClaimAsync(_topic, "s", 2, default);
        Assert.That(a.Concat(b).Select(d => d.MessageId), Is.EqualTo(published), "Each copy goes to one receiver, oldest first.");
        Assert.That((await store.GetStatsAsync(_topic, "s", default))!.Pending, Is.EqualTo(4), "Claimed but not acknowledged.");
    }

    [Test]
    public async Task Remove_DeletesTheSubscriptionAndWaitingCopies()
    {
        var store = Store();
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        await store.PublishAsync(_topic, Message(), default);

        Assert.That(await store.RemoveSubscriptionAsync(_topic, "s", default), Is.True);
        Assert.That(await store.RemoveSubscriptionAsync(_topic, "s", default), Is.False);
        Assert.That(await store.TouchSubscriptionAsync(_topic, "s", default), Is.False);
        Assert.That(await store.PublishAsync(_topic, Message(), default), Is.Zero);

        await store.UpsertSubscriptionAsync(_topic, "s", default);
        Assert.That(await store.ClaimAsync(_topic, "s", 10, default), Is.Empty, "Copies from before the removal are gone.");
    }

    [Test]
    public async Task Cleanup_RemovesExpiredSubscriptions()
    {
        var store = Store(o => o.SubscriptionIdleTimeout = TimeSpan.FromMilliseconds(500));
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        await store.PublishAsync(_topic, Message(), default);
        await Task.Delay(800);

        await store.CleanupAsync(default);
        Assert.That(await store.GetStatsAsync(_topic, "s", default), Is.Null);
        await store.UpsertSubscriptionAsync(_topic, "s", default);
        Assert.That(await store.ClaimAsync(_topic, "s", 10, default), Is.Empty);
    }
}

[TestFixture]
public class InMemoryTopicStoreTests : TopicStoreContractTests
{
    protected override ITopicStore CreateStore(TopicServerOptions options) =>
        new InMemoryTopicStore(options, NullLogger<InMemoryTopicStore>.Instance);
}

[TestFixture]
public class PostgresTopicStoreTests : TopicStoreContractTests
{
    private PostgreSqlContainer _container = null!;
    private readonly List<PostgresTopicStore> _stores = new();

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _container = new PostgreSqlBuilder().Build();
        await _container.StartAsync();
        await using var store = new PostgresTopicStore(new TopicServerOptions { ConnectionString = _container.GetConnectionString(), Schema = "topics" },
            NullLogger<PostgresTopicStore>.Instance);
        await store.MigrateAsync(default);
        await store.MigrateAsync(default); // idempotent
    }

    protected override ITopicStore CreateStore(TopicServerOptions options)
    {
        options.ConnectionString = _container.GetConnectionString();
        options.Schema = "topics";
        var store = new PostgresTopicStore(options, NullLogger<PostgresTopicStore>.Instance);
        _stores.Add(store);
        return store;
    }

    [TearDown]
    public async Task DisposeStoresAsync()
    {
        foreach (var store in _stores) await store.DisposeAsync();
        _stores.Clear();
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _container.DisposeAsync();
}

[TestFixture]
public class SqlServerTopicStoreTests : TopicStoreContractTests
{
    private MsSqlContainer _container = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _container = new MsSqlBuilder().Build();
        await _container.StartAsync();
        var store = new SqlServerTopicStore(new TopicServerOptions { ConnectionString = _container.GetConnectionString(), Schema = "topics" },
            NullLogger<SqlServerTopicStore>.Instance);
        await store.MigrateAsync(default);
        await store.MigrateAsync(default); // idempotent
    }

    protected override ITopicStore CreateStore(TopicServerOptions options)
    {
        options.ConnectionString = _container.GetConnectionString();
        options.Schema = "topics";
        return new SqlServerTopicStore(options, NullLogger<SqlServerTopicStore>.Instance);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _container.DisposeAsync();
}

[TestFixture]
public class RedisTopicStoreTests : TopicStoreContractTests
{
    private RedisContainer _container = null!;
    private ConnectionMultiplexer _redis = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _container = new RedisBuilder().Build();
        await _container.StartAsync();
        _redis = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    protected override ITopicStore CreateStore(TopicServerOptions options) =>
        new RedisTopicStore(options, _redis, NullLogger<RedisTopicStore>.Instance);

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await _redis.DisposeAsync();
        await _container.DisposeAsync();
    }
}
