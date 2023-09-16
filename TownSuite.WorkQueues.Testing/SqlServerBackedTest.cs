using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TownSuite.WorkQueues.Testing;

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
    }

    [Test]
    public async Task EnqueueAndDequeueTest1()
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

        var found = cn.QueryFirstOrDefault("select * from workqueue");
        Assert.That(found == null);
    }
}