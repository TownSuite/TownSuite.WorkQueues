
A simple work queue library backed by a sql database.  Processing of data requires polling the WorkQueue table.  **Do not use with a high load system**.

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


