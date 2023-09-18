using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TownSuite.WorkQueues.Testing;

[TestFixture]
public class SqlServerBackedTest
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
        using var cn = new SqlConnection(config.GetConnectionString("SqlServerDbConnection"));
        cn.Open();
        cn.Execute("delete from workqueue where channel='AUniqueChannelName';");
        cn.Execute("delete from workqueue where channel='NonDestructiveName';");
    }
    
    [Test]
    public async Task EnqueueAndDequeue_Test()
    {
        await using var cn = new SqlConnection(config.GetConnectionString("SqlServerDbConnection"));
        await cn.OpenAsync();

        var workQueue = new DbBackedWorkQueue();

        await workQueue.Enqueue("AUniqueChannelName",
            new { Hello = "world" }, cn, null);

        await using var txn = cn.BeginTransaction();
        var result = await workQueue.Dequeue<dynamic>("AUniqueChannelName", cn, txn);
        txn.Commit();
        Assert.That(string.Equals(result.ToString(), "{ Hello = world }"));

        var found = cn.QueryFirstOrDefault("select * from workqueue where channel = 'AUniqueChannelName'");
        Assert.That(found == null);
    }
    
    [Test]
    public async Task EnqueueAndDequeue_NonDestructive_Test()
    {
        await using var cn = new SqlConnection(config.GetConnectionString("SqlServerDbConnection"));
        await cn.OpenAsync();

        var workQueue = new DbBackedWorkQueue_NonDestructive();

        await workQueue.Enqueue("NonDestructiveName",
            new { Hello = "world" }, cn, null);
        await workQueue.Enqueue("NonDestructiveName",
            new { Hello = "The Second" }, cn, null);

        await using var txn1 = cn.BeginTransaction();
        var result1 = await workQueue.Dequeue<dynamic>("NonDestructiveName", cn, txn1);
        var result2 = await workQueue.Dequeue<dynamic>("NonDestructiveName", cn, txn1);
        txn1.Commit();
        Assert.That(string.Equals(result2.ToString(), "{ Hello = The Second }"));

        var found = cn.QueryFirstOrDefault<dynamic>("select * from workqueue where channel = 'NonDestructiveName'");
        Assert.That(found != null);
        Assert.That(found.Channel == "NonDestructiveName");
        Assert.That(found.Payload.Contains("\"Hello\": \"world\""));
        Assert.That(found.timeprocessedutc != null);
        
        
   

    }
}