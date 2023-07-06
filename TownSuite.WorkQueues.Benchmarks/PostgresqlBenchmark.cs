using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TownSuite.WorkQueues.Benchmarks;

[SimpleJob(RuntimeMoniker.Net60, baseline: true)]
public class PostgresqlBenchmark : IBenchmark
{
    private IConfiguration config;
    
    [GlobalSetup]
    public void Setup()
    {
        config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .Build();
    }

    [Benchmark]
    public async Task Enqueue()
    {
        await using var cn = new NpgsqlConnection(config.GetConnectionString("PostgresqlDbConnection"));
        await cn.OpenAsync();

        var workQueue = new DbBackedWorkQueue();
        
        await workQueue.Enqueue("AUniqueChannelName",
            new {Hello = "world"}, cn, null);
    }
}