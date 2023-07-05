using System.Data;
using System.Data.Common;
using System.Reflection.Metadata.Ecma335;
using Newtonsoft.Json;

namespace TownSuite.WorkQueues;

public class DbBackedWorkQueue
{
    // http://rusanu.com/2010/03/26/using-tables-as-queues/
    // https://stackoverflow.com/questions/24224093/what-is-the-use-of-these-keyword-in-sql-server-updlock-rowlock-readpast
    // UPDLOCK places update locks on rows that are being selected until the end of the transaction. Other transaction cannot update or delete the row but they are allowed to select it.
    // ROWLOCK places locks on row level opposed to a page or table lock.
    // READPAST Records that are locked are not returned 

    public async Task<bool> Enqueue<T>(string channel, T payload, IDbConnection con, IDbTransaction txn = null)
    {
        return await Enqueue<T>(channel, payload, con as DbConnection, txn == null ? null : txn as DbTransaction);
    }

    public async Task<bool> Enqueue<T>(string channel, T payload, DbConnection con, DbTransaction txn = null)
    {
        object responseDefaultDataEvenWhenNull = default(T);
        string jsonPayload = JsonConvert.SerializeObject(payload, Formatting.Indented, new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All
        });
        string serializedPlaceholderResponse = JsonConvert.SerializeObject(responseDefaultDataEvenWhenNull,
            Formatting.Indented, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            });

        if (con.State == ConnectionState.Closed)
            await con.OpenAsync();

        await using (var command = con.CreateCommand())
        {
            command.CommandText = "WorkQueue_Enqueue";
            command.CommandType = CommandType.StoredProcedure;
            command.Transaction = txn;

            IDbDataParameter channelParam = command.CreateParameter();
            channelParam.ParameterName = "@Channel";
            channelParam.Value = channel;
            command.Parameters.Add(channelParam);

            IDbDataParameter payloadParam = command.CreateParameter();
            payloadParam.ParameterName = "@Payload";
            payloadParam.Value = jsonPayload;
            command.Parameters.Add(payloadParam);

            int rowsAffected = await command.ExecuteNonQueryAsync();

            return rowsAffected > 0;
        }
    }


    public async Task<T> Dequeue<T>(string channel, IDbConnection con, IDbTransaction txn, int offset = 0)
    {
        return await Dequeue<T>(channel, con as DbConnection, txn == null ? null : txn as DbTransaction, offset);
    }

    public async Task<T> Dequeue<T>(string channel, DbConnection con, DbTransaction txn, int offset = 0)
    {
        if (con.State == ConnectionState.Closed) await con.OpenAsync();

        using (var command = con.CreateCommand())
        {
            command.CommandText = "WorkQueue_Dequeue";
            command.CommandType = CommandType.StoredProcedure;
            command.Transaction = txn;

            var channelParameter = command.CreateParameter();
            channelParameter.ParameterName = "@channel";
            channelParameter.Value = channel;
            command.Parameters.Add(channelParameter);

            var offsetParameter = command.CreateParameter();
            offsetParameter.ParameterName = "@offset";
            offsetParameter.Value = offset;
            command.Parameters.Add(offsetParameter);

            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    string jsonPayload = reader.GetString(0);

                    if (string.IsNullOrWhiteSpace(jsonPayload))
                    {
                        return default(T);
                    }

                    var settings = new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Auto
                    };

                    return JsonConvert.DeserializeObject<T>(jsonPayload, settings);
                }
            }
        }

        return default(T);
    }
}