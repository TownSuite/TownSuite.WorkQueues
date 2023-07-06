namespace TownSuite.WorkQueues.Benchmarks;

public interface IBenchmark
{
    Task Enqueue();
}