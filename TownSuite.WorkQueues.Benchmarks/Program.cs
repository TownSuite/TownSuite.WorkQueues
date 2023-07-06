// See https://aka.ms/new-console-template for more information

using System.Diagnostics;
using BenchmarkDotNet.Running;
using TownSuite.WorkQueues.Benchmarks;

var summary = BenchmarkRunner.Run(typeof(Program).Assembly);


var postgre = new PostgresqlBenchmark();
postgre.Setup();

ConcurrentCallCount(postgre, threadCount: 1, durationInSeconds: 30);
ConcurrentCallCount(postgre, threadCount: 10, durationInSeconds: 30);
ConcurrentCallCount(postgre, threadCount: 20, durationInSeconds: 30);
ConcurrentCallCount(postgre, threadCount: 30, durationInSeconds: 30);
ConcurrentCallCount(postgre, threadCount: 40, durationInSeconds: 30);
ConcurrentCallCount(postgre, threadCount: 50, durationInSeconds: 30);


var sqlServer = new SqlServerBenchmark();
sqlServer.Setup();

ConcurrentCallCount(sqlServer, threadCount: 1, durationInSeconds: 30);
ConcurrentCallCount(sqlServer, threadCount: 20, durationInSeconds: 30);
ConcurrentCallCount(sqlServer, threadCount: 20, durationInSeconds: 30);
ConcurrentCallCount(sqlServer, threadCount: 30, durationInSeconds: 30);
ConcurrentCallCount(sqlServer, threadCount: 40, durationInSeconds: 30);
ConcurrentCallCount(sqlServer, threadCount: 50, durationInSeconds: 30);

void ConcurrentCallCount(IBenchmark inst, int threadCount, int durationInSeconds)
{
    Stopwatch stopwatch = new Stopwatch();

// Start the stopwatch
    stopwatch.Start();

    int functionCallCount = 0;
    int errors = 0;

// Create an array to hold the threads
    Thread[] threads = new Thread[threadCount];

// Create and start the threads
    for (int i = 0; i < threadCount; i++)
    {
        threads[i] = new Thread(async () =>
        {
            while (stopwatch.Elapsed.TotalSeconds < durationInSeconds)
            {
                try
                {
                    inst.Enqueue().Wait(); // Replace this with your actual function

                    Interlocked.Increment(ref functionCallCount);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    Console.WriteLine(ex);
                }
            }
        });

        threads[i].Start();
    }

    foreach (var thread in threads)
    {
        thread.Join();
    }

    stopwatch.Stop();
    double callRate = functionCallCount / stopwatch.Elapsed.TotalSeconds;


    Console.WriteLine(
        $"Function calls per second (Threads: {threadCount}, Duration: {durationInSeconds}, DbType {inst.GetType().Name}): {callRate:N0}, Total Calls: {functionCallCount:N0}, Errors: {errors:N0}");
}