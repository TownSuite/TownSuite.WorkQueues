---
layout: docs
title: SQL Server Backend — TownSuite.WorkQueues
description: Set up the SQL Server-backed message bus with UPDLOCK + READPAST concurrency.
permalink: /docs/backends/sqlserver/
---

# SQL Server Backend

The SQL Server backend uses `UPDLOCK + ROWLOCK + READPAST` table hints so that multiple consumer processes safely claim disjoint sets of messages without blocking each other.

## Install

```sh
dotnet add package TownSuite.WorkQueues.SqlServer
```

## Schema migration

The migration hosted service creates the `workqueue` table, index, and stored procedures on startup:

```csharp
builder.Services.AddSingleton(new SqlServerTransportOptions
{
    ConnectionString = "Server=.;Database=myapp;Integrated Security=true",
    Schema = "dbo"
});
builder.Services.AddSqlServerMigrationHostedService();
```

To run migrations with broader permissions than the runtime connection, set `AdminConnectionString` separately:

```csharp
new SqlServerTransportOptions
{
    ConnectionString = "...",
    AdminConnectionString = "Server=.;Database=myapp;User Id=sa;Password=..."
}
```

Alternatively, apply the scripts in `scripts/sql-server/` manually:

- `dbo.WorkQueue.sql` — table and index
- `dbo.WorkQueue_Enqueue.sql` — enqueue stored procedure
- `dbo.WorkQueue_Dequeue.sql` — dequeue stored procedure


<div class="callout">
<strong>Upgrading to the current version?</strong> The migration adds <code>priority</code> and <code>tracecontext</code> columns that every claim query reads. If you manage the schema yourself, add them first. See <a href="{{ '/docs/delivery-controls/' | relative_url }}#upgrading">Delivery controls → Upgrading</a>.
</div>

## Register the bus

```csharp
builder.Services.AddSqlServerMessageBus((sp, bus) =>
{
    // singleton consumer instance
    bus.Subscribe(new OrderConsumer());

    // or DI-scoped — resolved from a fresh scope per message
    bus.Subscribe<OrderSubmitted, OrderConsumer>();
});
```

## Options reference

See [`SqlServerTransportOptions`]({{ '/docs/configuration/' | relative_url }}#sql-server--sqlservertransportoptions) for the full property table. Common values:

```csharp
new SqlServerTransportOptions
{
    ConnectionString = "...",
    Schema = "dbo",              // default
    MaxBatchSize = 50,
    MaxWaitTime = TimeSpan.FromSeconds(2),
    MaxRetries = 3,
    RetryDelay = TimeSpan.FromSeconds(15)
}
```

## How claiming works

Each poll cycle opens a transaction and runs:

```sql
SELECT TOP (@maxMessages) id, channel, payload, retrycount, messageid, timecreatedutc
FROM [dbo].[workqueue] WITH (UPDLOCK, ROWLOCK, READPAST)
WHERE timeprocessedutc IS NULL
  AND failedat IS NULL
  AND (scheduledfor IS NULL OR scheduledfor <= GETUTCDATE())
  AND channel IN (@ch0, @ch1, ...)
ORDER BY timecreatedutc
```

`READPAST` causes other connections to skip over rows that are already locked by this transaction. `UPDLOCK` promotes the read lock to an update lock, preventing two concurrent readers from both claiming the same row. The lock releases when the transaction commits at the end of the batch. A crash triggers an implicit rollback, making rows immediately available.
