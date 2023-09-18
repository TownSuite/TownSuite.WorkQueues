using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TownSuite.WorkQueues.Testing;

[TestFixture]
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
        Cleanup();
    }
    private void Cleanup()
    {
        using var cn = new NpgsqlConnection(config.GetConnectionString("PostgresqlDbConnection"));
        cn.Open();
        cn.Execute("delete from workqueue where channel='AUniqueChannelName';");
        cn.Execute("delete from workqueue where channel='NonDestructiveName';");
    }
    
    [Test]
    public async Task EnqueueAndDequeue_Test()
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
        var found = cn.QueryFirstOrDefault("select * from workqueue where channel = 'AUniqueChannelName'");
        Assert.That(found == null, "is null");
    }
    
    
    [Test]
    public async Task EnqueueAndDequeue_NonDestructive_Test()
    {
        await using var cn = new NpgsqlConnection(config.GetConnectionString("PostgresqlDbConnection"));
        await cn.OpenAsync();

        var workQueue = new DbBackedWorkQueue_NonDestructive();
        
        await workQueue.Enqueue<TestValue>("NonDestructiveName",
            new TestValue {Hello = "world"}, cn, null);
        
        await using var txn = cn.BeginTransaction();
        var result = await workQueue.Dequeue<TestValue>("NonDestructiveName", cn, txn);
        txn.Commit();    
        Assert.That(string.Equals(result.Hello, "world"));
        var found = cn.QueryFirstOrDefault<dynamic>("select * from workqueue where channel = 'NonDestructiveName'");
        Assert.That(found != null);
        Assert.That(found.channel == "NonDestructiveName");
        Assert.That(found.payload.Contains("\"Hello\": \"world\""));
        Assert.That(found.timeprocessedutc != null);
    }
}