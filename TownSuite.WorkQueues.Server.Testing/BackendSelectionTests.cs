using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TownSuite.WorkQueues.Server.Stores;

namespace TownSuite.WorkQueues.Server.Testing;

/// <summary>WorkQueuesServer:Backend picks the store.</summary>
[TestFixture]
public class BackendSelectionTests
{
    [TestCase("sqlserver", typeof(SqlServerTopicStore))]
    [TestCase("SqlServer", typeof(SqlServerTopicStore))]
    [TestCase("postgres", typeof(PostgresTopicStore))]
    [TestCase("redis", typeof(RedisTopicStore))]
    [TestCase("memory", typeof(InMemoryTopicStore))]
    public void Backend_SelectsTheStore(string backend, Type expected)
    {
        var services = new ServiceCollection().AddTopicServer(Configuration(backend));
        var registration = services.Single(d => d.ServiceType == typeof(ITopicStore));
        Assert.That(registration.ImplementationType, Is.EqualTo(expected));
    }

    [Test]
    public void UnknownBackend_FailsAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTopicServer(Configuration("oracle")));
        Assert.That(ex!.Message, Does.Contain("sqlserver, postgres, redis"));
    }

    private static IConfiguration Configuration(string backend) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkQueuesServer:Backend"]          = backend,
            ["WorkQueuesServer:ConnectionString"] = "Server=unused",
            ["WorkQueuesServer:Jwt:Issuer"]       = "https://auth.test",
            ["WorkQueuesServer:Jwt:Audience"]     = "workqueues",
            ["WorkQueuesServer:Jwt:SigningKey"]   = "test-signing-key-that-is-at-least-32-bytes-long"
        })
        .Build();
}
