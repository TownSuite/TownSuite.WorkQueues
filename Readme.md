
A simple work queue library backed by a sql database.  Processing of data requires polling the WorkQueue table.  Do not use with a high load system.

Providing alternative backends based on tools such as kafka, redis, or rabbitmq is left as an exercise for the reader.



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


