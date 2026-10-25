---
layout: docs
title: PostgreSQL Backend — TownSuite.WorkQueues
description: Set up the PostgreSQL-backed message bus with FOR UPDATE SKIP LOCKED concurrency.
permalink: /docs/backends/postgres/
---

# PostgreSQL Backend

The PostgreSQL backend uses `FOR UPDATE SKIP LOCKED` so that multiple consumer processes claim disjoint rows without blocking each other. It is the fastest backend in published benchmarks, reaching roughly 19,000 calls per second at 50 threads.

## Install

```sh
dotnet add package TownSuite.WorkQueues.Postgres
```

## Schema migration

The migration hosted service creates the `workqueue` table, index, and stored procedures on startup. It is idempotent and safe to run against an existing database.

```csharp
builder.Services.AddSingleton(new SqlTransportOptions
{
    ConnectionString = "Host=localhost;Database=myapp;Username=app;Password=secret",
    Schema = "transport"
});
builder.Services.AddPostgresMigrationHostedService();
```

Alternatively, run the bundled scripts manually from `scripts/postgresql/` in the repository.

<div class="callout">
<strong>Upgrading to the current version?</strong> The migration adds <code>priority</code> and <code>tracecontext</code> columns that every claim query reads. If you manage the schema yourself, add them first. See <a href="{{ '/docs/delivery-controls/' | relative_url }}#upgrading">Delivery controls → Upgrading</a>.
</div>

## Register the bus

```csharp
builder.Services.AddPostgresMessageBus((sp, bus) =>
{
    // singleton consumer instance
    bus.Subscribe(new OrderConsumer());

    // or DI-scoped — resolved from a fresh scope per message
    bus.Subscribe<OrderSubmitted, OrderConsumer>();
});
```

`AddPostgresMessageBus` registers `PostgresMessageBus` as the `IMessageBus` singleton. The factory delegate runs once at construction time; register all subscriptions here before the bus starts polling.

## Options reference

See [`SqlTransportOptions`]({{ '/docs/configuration/' | relative_url }}#postgresql--sqltransportoptions) for the full property table. Common values:

```csharp
new SqlTransportOptions
{
    ConnectionString = "...",
    Schema = "transport",        // default — change to match your schema
    MaxBatchSize = 50,           // messages per poll cycle
    MaxWaitTime = TimeSpan.FromSeconds(2),
    MaxRetries = 5,
    RetryDelay = TimeSpan.FromSeconds(30)
}
```

## How claiming works

Each poll cycle opens a transaction and runs:

```sql
SELECT id, channel, payload, retrycount, messageid, timecreatedutc
FROM transport.workqueue
WHERE timeprocessedutc IS NULL
  AND failedat IS NULL
  AND (scheduledfor IS NULL OR scheduledfor <= CURRENT_TIMESTAMP)
  AND channel = ANY(@channels)
ORDER BY timecreatedutc
FOR UPDATE SKIP LOCKED
LIMIT @maxMessages
```

`FOR UPDATE SKIP LOCKED` ensures that two processes polling simultaneously claim disjoint rows. The lock is held until the transaction commits at the end of the batch. If the process crashes mid-batch, the transaction rolls back and all claimed rows are immediately available to other consumers — no lock timeout needed.
