using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TownSuite.WorkQueues.Testing;

public class PostgresqlBackedTest
{
    private IConfiguration config;
    
    [SetUp]
    public void Setup()
    {
        config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .Build();
    }

    [Test]
    public async Task EnqueueAndDequeueTest1()
    {
        await using var cn = new NpgsqlConnection(config.GetConnectionString("PostgresqlDbConnection"));
        await cn.OpenAsync();

        var workQueue = new DbBackedWorkQueue();
        
        await workQueue.Enqueue<TestValue>("AUniqueChannelName",
            new TestValue {Hello = "world"}, cn, null);
        
        await using var txn = cn.BeginTransaction();
        var result = await workQueue.Dequeue<TestValue>("AUniqueChannelName", cn, txn);
        txn.Commit();    
        Assert.That(string.Equals(result.Hello, "world"));
        var found = cn.QueryFirstOrDefault("select * from workqueue");
        Assert.That(found == null, "is null");
    }
}