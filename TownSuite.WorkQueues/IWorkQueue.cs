using System.Data;
using System.Data.Common;

namespace TownSuite.WorkQueues;

public interface IWorkQueue
{
    Task<bool> Enqueue<T>(string channel, T payload, IDbConnection con, IDbTransaction txn = null);
    Task<bool> Enqueue<T>(string channel, T payload, DbConnection con, DbTransaction txn = null);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="channel">Group work for different background workers</param>
    /// <param name="con"></param>
    /// <param name="txn"></param>
    /// <param name="offset">Useful to skip records in failure cases or if multiple instances of workers need to work on the same channel.</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T> Dequeue<T>(string channel, IDbConnection con, IDbTransaction txn, int offset = 0);

    Task<T> Dequeue<T>(string channel, DbConnection con, DbTransaction txn, int offset = 0);
}