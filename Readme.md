
A simple work queue library backed by a sql database.  Processing of data requires polling the WorkQueue table.  **Do not use with a high load system**.  See the **Benchmarks** section below for a definition of a low load system.

Providing alternative backends based on tools such as kafka, redis, or rabbitmq is left as an exercise for the reader.


# nuget package


Build the project in Release mode. It will produce a nuget package in the bin folder. Upload it to your nuget repository or point the nuget source at the folder. Have fun.

```powershell
dotnet add package "TownSuite.WorkQueue" --source "C:\the\folder\with\the\nuget\package\TownSuite.WorkQueue.nupkg"
```


# Example


```cs
 public async Task SaveTheData<T>(T request, DbConnection cn, IWorkQueue _workQueue)
  {
    await _workQueue.Enqueue("AUniqueChannelName",
        request, cn, null);
  }

// dequque and process records.  Skip but log failed records.
public async Task ProcessTheData<T>(T request, DbConnection cn, IWorkQueue _workQueue)
{
    int offset = 0;
    do
    {
        try
        {
            using var txn = cn.BeginTransaction();

            data =
                await _workQueue.Dequeue<dynamic>("AUniqueChannelName", cn, txn,
                    offset);

            if (data == null)
            {
                return;
            }

            // Process the "data" here
            Console.WriteLine(data.ToString());

            txn.Commit();
        }
        catch (Exception ex)
        {
            // increase the offset to skip the failed record
            offset = offset + 1;
            Console.Error.WriteLine(ex);
        }
      } while (request != null);
}
```


# TownSuite.WorkQueues.Testing Instructions

Create a sql server and postgresql database.   Update appsetting.json connection string.

Run the scripts in the following order.  Once the database and scripts are run the nunit tests can be run.

* postgresql/
    * public.WorkQueue.sql
    * public.WorkQueue_Enqueue.sql
    * public.WorkQueue_Dequeue.sql
* sql-server/
    * dbo.WorkQueue.sql
    * dbo.WorkQueue_Enqueue.sql
    * dbo.WorkQueue_Dequeue.sql




# Benchmarks

Setup

client computer -> sql computer


Test with throughput limits:
* <= 10000 calls per second
* burstable up to 
  * ~19000 calls per second for postgresql 
  * ~13000 calls per second for sql server


## Postgresql


calls per second


| Threads| 1 second   |  30 second | 
|---|---|---|
| 1 | 1,953   | 58,590 |
| 10 | 11,509 | 345,278 | 
| 20 | 16,661 | 499,844 | 
| 30 | 18,062 | 541,895 |
| 40 | 18,366 | 551,004 | 
| 50 | 19,090 | 572,727 | 


``` ini

BenchmarkDotNet=v0.13.5, OS=macOS Ventura 13.4.1 (22F82) [Darwin 22.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK=6.0.408
  [Host]   : .NET 6.0.16 (6.0.1623.17311), Arm64 RyuJIT AdvSIMD
  .NET 6.0 : .NET 6.0.16 (6.0.1623.17311), Arm64 RyuJIT AdvSIMD

Job=.NET 6.0  Runtime=.NET 6.0  

```
|  Method |     Mean |    Error |   StdDev | Ratio |
|-------- |---------:|---------:|---------:|------:|
| Enqueue | 519.1 μs | 15.16 μs | 44.47 μs |  1.00 |



## SqlServer

calls per second

| Threads| 1 second   |  30 second | 
|---|---|---|
| 1 | 1,241 | 37,228 |
| 10 | 12,606 | 378,272 | 
| 20 | 12,943 | 388,693 | 
| 30 | 13,135 | 394,284 |
| 40 | 13,210 | 396,515 | 
| 50 | 12,944 | 388,545 | 


``` ini

BenchmarkDotNet=v0.13.5, OS=macOS Ventura 13.4.1 (22F82) [Darwin 22.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK=6.0.408
  [Host]   : .NET 6.0.16 (6.0.1623.17311), Arm64 RyuJIT AdvSIMD
  .NET 6.0 : .NET 6.0.16 (6.0.1623.17311), Arm64 RyuJIT AdvSIMD

Job=.NET 6.0  Runtime=.NET 6.0  

```
|  Method |     Mean |    Error |   StdDev | Ratio |
|-------- |---------:|---------:|---------:|------:|
| Enqueue | 794.8 μs | 12.84 μs | 12.01 μs |  1.00 |
