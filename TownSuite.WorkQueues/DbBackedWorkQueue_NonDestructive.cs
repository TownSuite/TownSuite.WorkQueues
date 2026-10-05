using System.Data;
using System.Data.Common;

namespace TownSuite.WorkQueues;

/// <summary>
/// Non-destructive variant of <see cref="DbBackedWorkQueue"/>. Dequeued rows are
/// <strong>kept</strong> in the table and stamped with <c>timeprocessedutc</c> on commit
/// rather than deleted. Use this when you need an audit trail or the ability to reprocess rows.
/// </summary>
/// <remarks>
/// Internally calls the <c>workqueue_dequeue_nondestructive</c> stored procedure.
/// Multiple workers can safely share a channel by incrementing the <c>offset</c> parameter
/// when a row cannot be processed.
/// </remarks>
[Obsolete("Legacy work queue API. Use IMessageBus: Publish/Subscribe, or Publish(message, connection, transaction) to enqueue inside your own transaction. See MIGRATING.md.", DiagnosticId = "TSWQ001")]
public class DbBackedWorkQueue_NonDestructive : DbBackedWorkQueue
{
    [return: System.Diagnostics.CodeAnalysis.MaybeNull]
    public override async Task<T> Dequeue<T>(string channel, IDbConnection con, IDbTransaction txn, int offset = 0)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNullOrEmpty(channel, nameof(channel));
#endif
        ArgumentNullException.ThrowIfNull(con, nameof(con));
        ArgumentNullException.ThrowIfNull(txn, nameof(txn));

        if (con is not DbConnection connection)
            throw new WorkQueuesException("con must be a DbConnection");

        if (txn is not DbTransaction transaction)
            throw new WorkQueuesException("txn must be a DbTransaction");

#pragma warning disable CS8602 // [MaybeNull] — caller contract matches callee, null flow is intentional
        return await Dequeue<T>(channel, connection, transaction, offset);
#pragma warning restore CS8602
    }

    [return: System.Diagnostics.CodeAnalysis.MaybeNull]
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

        // PostgreSQL CALL returns OUT parameters as a result-set row.
        // SQL Server EXEC with OUTPUT sets the parameter value after the reader is closed.
        // Both paths are covered: read from the reader first, then fall back to the parameter.
        string? jsonPayload = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync() && !reader.IsDBNull(0))
                jsonPayload = reader.GetString(0);
        }
        jsonPayload ??= payloadParameter.Value?.ToString();

        if (string.IsNullOrWhiteSpace(jsonPayload))
            return default!;

        return LegacyJsonDeserializer.Deserialize<T>(jsonPayload)!;
    }
}
