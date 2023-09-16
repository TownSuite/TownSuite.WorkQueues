using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace TownSuite.WorkQueues.Testing;

[TestFixture]
public class DbBackedBackedTest
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
    public async Task DequeueMustHaveTransactionTest1()
    {
        await using var cn = new SqlConnection(config.GetConnectionString("SqlServerDbConnection"));
        await cn.OpenAsync();

        var workQueue = new DbBackedWorkQueue();

        Assert.ThrowsAsync<WorkQueuesException>(async () =>
        {
            var result = await workQueue.Dequeue<dynamic>("AUniqueChannelName", cn, null);
        });
    }
}
