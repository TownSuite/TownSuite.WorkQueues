using System.Data;
using System.Data.Common;
using System.Reflection.Metadata.Ecma335;
using Newtonsoft.Json;

namespace TownSuite.WorkQueues;

public class DbBackedWorkQueue_NonDestructive : DbBackedWorkQueue, IWorkQueue
{
    public override async Task<T> Dequeue<T>(string channel, IDbConnection con, IDbTransaction txn, int offset = 0)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNullOrEmpty(channel, nameof(channel));
#endif
        ArgumentNullException.ThrowIfNull(con, nameof(con));
        ArgumentNullException.ThrowIfNull(txn, nameof(txn));
        
        if (con is not DbConnection connection)
        {
            throw new WorkQueuesException("con must be a DbConnection");
        }
        
        if (txn is not DbTransaction transaction)
        {
            throw new WorkQueuesException("txn must be a DbTransaction");
        }
        
        return await Dequeue<T>(channel, connection, transaction, offset);
    }

    public override async Task<T> Dequeue<T>(string channel, DbConnection con, DbTransaction txn, int offset = 0)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNullOrEmpty(channel, nameof(channel));
#endif
        ArgumentNullException.ThrowIfNull(con, nameof(con));
        ArgumentNullException.ThrowIfNull(txn, nameof(txn));
        
        if (con.State == ConnectionState.Closed) await con.OpenAsync();

        await using var command = con.CreateCommand();
        command.CommandText = "workqueue_dequeue_nondestructive";
        command.CommandType = CommandType.StoredProcedure;
        command.Transaction = txn;

        var channelParameter = command.CreateParameter();
        channelParameter.ParameterName = "@p_channel";
        channelParameter.Value = channel;
        command.Parameters.Add(channelParameter);

        var offsetParameter = command.CreateParameter();
        offsetParameter.ParameterName = "@p_offset";
        offsetParameter.Value = offset;
        command.Parameters.Add(offsetParameter);

        var payloadParameter = command.CreateParameter();
        payloadParameter.ParameterName = "@p_payload";
        payloadParameter.DbType = DbType.String;
        payloadParameter.Size = int.MaxValue;
        payloadParameter.Direction = ParameterDirection.Output;
        command.Parameters.Add(payloadParameter);

        await using var reader = await command.ExecuteReaderAsync();
        string jsonPayload = payloadParameter.Value?.ToString()!;

        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return default!;
        }

        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto
        };

        return JsonConvert.DeserializeObject<T>(jsonPayload, settings)!;
    }
}