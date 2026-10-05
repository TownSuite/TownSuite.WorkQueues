
A SQL-backed work queue library for low-load .NET systems. Processing requires polling the workqueue table. **Do not use in high-throughput systems** — see the Benchmarks section for what "low load" means.

For higher throughput, consider purpose-built brokers such as Kafka, Redis Streams, or RabbitMQ.

---

## Quick Start

The fastest path to a working message bus. Requires a running PostgreSQL instance.
For Redis, see the [Redis Backend](#redis-backend) section.

**1. Install**

```bash
dotnet add package TownSuite.WorkQueues
dotnet add package TownSuite.WorkQueues.Postgres
```

**2. Define a message**

```csharp
public class OrderSubmitted
{
    public Guid   OrderId  { get; set; }
    public string Customer { get; set; } = string.Empty;
}
```

**3. Write a consumer**

```csharp
public class OrderConsumer : IConsumer<OrderSubmitted>
{
    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        Console.WriteLine($"Order received: {context.Message.OrderId}");
        return Task.CompletedTask;
        // Throw any exception to retry. After MaxRetries the message is dead-lettered.
    }
}
```

**4. Wire it up (Worker Service / ASP.NET Core)**

```csharp
// Program.cs
builder.Services.AddSingleton(new SqlTransportOptions
{
    ConnectionString      = "Host=localhost;Database=myapp;Username=app;Password=secret",
    AdminConnectionString = "Host=localhost;Database=myapp;Username=admin;Password=secret",
    Schema     = "transport",
    MaxRetries = 3
});

// Creates the workqueue table and stored procedures on first startup.
builder.Services.AddPostgresMigrationHostedService();

// Bus singleton — subscribe all consumers here before the bus starts.
builder.Services.AddSingleton<IMessageBus>(sp =>
{
    var bus = new PostgresMessageBus(
        sp.GetRequiredService<SqlTransportOptions>(),
        sp.GetRequiredService<ILogger<PostgresMessageBus>>());

    bus.Subscribe(new OrderConsumer());
    return bus;
});

// Resolves the bus after migrations complete and disposes it on shutdown.
builder.Services.AddHostedService<MessageBusHostedService>();
```

`MessageBusHostedService` is a small wrapper you add once to every project — see
[WORKER_SERVICES.md](WORKER_SERVICES.md) for the full implementation (10 lines).

**5. Publish a message**

```csharp
// From a controller, minimal API endpoint, or any service with IMessageBus injected:
await bus.Publish(new OrderSubmitted
{
    OrderId  = Guid.NewGuid(),
    Customer = "alice@example.com"
});
```

The message is written to the database and delivered to `OrderConsumer` on the next polling
cycle (within `MaxWaitTime`, default 5 s).

---

## Contents

- [Quick Start](#quick-start)
- [NuGet Package](#nuget-package)
- [Database Setup & Migrations](#database-setup--migrations)
- [Work Queue (direct enqueue/dequeue)](#work-queue-direct-enqueuededequeue)
- [Message Bus (publish/subscribe)](#message-bus-publishsubscribe)
- [SQL Server Message Bus](#sql-server-message-bus)
- [SQLite Backend (local development)](#sqlite-backend-local-development)
- [Redis Backend](#redis-backend)
- [Dead-Letter Queue & Retries](#dead-letter-queue--retries)
  - [Programmatic replay via ReplayDeadLettered\<T\>](#programmatic-replay-via-replaydeadletteredt)
  - [Listing dead-lettered messages](#listing-dead-lettered-messages)
  - [Fault delivery guarantees](#fault-delivery-guarantees)
  - [Retry backoff](#retry-backoff)
  - [Non-retryable exceptions](#non-retryable-exceptions)
  - [Checking bus health with IsPolling](#checking-bus-health-with-ispolling)
- [Message Expiry](#message-expiry)
- [Transactional Publish (outbox)](#transactional-publish-outbox)
- [Concurrency](#concurrency)
  - [Lease-based claiming](#lease-based-claiming)
- [Purging Old Messages](#purging-old-messages)
- [Monitoring: Metrics & Queue Statistics](#monitoring-metrics--queue-statistics)
  - [Health checks](#health-checks)
- [Configuration Reference](#configuration-reference)
- [Running the Tests](#running-the-tests)
- [Upgrading from Earlier Versions](#upgrading-from-earlier-versions)
- [Benchmarks](#benchmarks)
- [Migration guide — direct WorkQueue → message bus](MIGRATING.md)
- [Worker Service & ASP.NET Core integration guide](WORKER_SERVICES.md)

---

## NuGet Package

Build in Release mode to produce a NuGet package, then reference it from your local feed:

```powershell
dotnet add package "TownSuite.WorkQueues" --source "C:\path\to\package\folder"
```

---

## Database Setup & Migrations

### PostgreSQL — automatic migrations on startup

Register the hosted service in your DI container. It creates the schema, table, stored procedures, and index on first run, and is safe to run on every subsequent startup.

```cs
// Program.cs / Startup.cs
builder.Services.AddSingleton(new SqlTransportOptions
{
    ConnectionString      = "Host=...;Database=mydb;Username=app;Password=...",
    AdminConnectionString = "Host=...;Database=mydb;Username=admin;Password=...",
    Schema                = "transport",   // default
    MaxBatchSize          = 100,           // default
    MaxWaitTime           = TimeSpan.FromSeconds(5), // default
    MaxRetries            = 3              // default — messages exceeding this are dead-lettered
});

builder.Services.AddPostgresMigrationHostedService();
```

### SQL Server — manual scripts

Run the scripts in `scripts/sql-server/` in this order against your database:

1. `dbo.WorkQueue.sql`
2. `dbo.WorkQueue_Enqueue.sql`
3. `dbo.WorkQueue_Dequeue.sql`
4. `dbo.WorkQueue_Dequeue_NonDestructive.sql`

---

## Work Queue (direct enqueue/dequeue)

> **Legacy API.** `IWorkQueue`, `DbBackedWorkQueue`, `DbBackedWorkQueue_NonDestructive` and the Redis
> `IRedisWorkQueue` are marked `[Obsolete]` with diagnostic id `TSWQ001`. New code should use the
> [message bus](#message-bus-publishsubscribe); to enqueue inside your own transaction use
> [transactional publish](#transactional-publish-outbox). See [MIGRATING.md](MIGRATING.md). Existing
> callers keep working; suppress the warning with `<NoWarn>$(NoWarn);TSWQ001</NoWarn>` while migrating.

Both PostgreSQL and SQL Server are supported. Inject `IWorkQueue` and use any open `DbConnection`.

### Enqueue

```cs
// txn is optional; pass null to auto-commit immediately
await workQueue.Enqueue("orders", new OrderPayload { Id = 42 }, cn, txn: null);
```

### Dequeue — destructive (record deleted on retrieval)

```cs
using var txn = cn.BeginTransaction();

var order = await workQueue.Dequeue<OrderPayload>("orders", cn, txn);
if (order is null)
{
    txn.Rollback();
    return; // queue empty
}

// process order ...
txn.Commit();
```

### Dequeue — non-destructive (record kept, marked with timeprocessedutc)

```cs
var workQueue = new DbBackedWorkQueue_NonDestructive();

using var txn = cn.BeginTransaction();
var order = await workQueue.Dequeue<OrderPayload>("orders", cn, txn);
txn.Commit();
```

### Skipping failed records with offset

The `offset` parameter lets multiple workers share a channel without blocking on each other, or lets a single worker skip a problematic record and come back to it later:

```cs
int offset = 0;
while (true)
{
    using var txn = cn.BeginTransaction();
    var item = await workQueue.Dequeue<Payload>("channel", cn, txn, offset);

    if (item is null) break;

    try
    {
        Process(item);
        txn.Commit();
        offset = 0; // reset on success
    }
    catch (Exception ex)
    {
        txn.Rollback();
        logger.LogError(ex, "Failed at offset {Offset}", offset);
        offset++;   // skip this record next iteration
    }
}
```

> **Channel name limit:** channel names must not exceed 500 characters. The channel column is `VARCHAR(500)` in both databases.

---

## Message Bus (publish/subscribe)

`PostgresMessageBus` provides a lightweight pub/sub layer backed by the same workqueue table. A background polling loop claims messages using `FOR UPDATE SKIP LOCKED` and dispatches them to in-memory consumers.

### Consumers

```cs
public class OrderConsumer : IConsumer<OrderSubmitted>
{
    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        var order = context.Message;
        Console.WriteLine($"Processing order {order.OrderId}");
        await Task.CompletedTask;
    }
}
```

### Wiring up the bus (standalone / console)

```cs
var options = new SqlTransportOptions
{
    ConnectionString      = connectionString,
    AdminConnectionString = connectionString,
    Schema                = "transport",
    MaxBatchSize          = 50,
    MaxWaitTime           = TimeSpan.FromSeconds(2),
    MaxRetries            = 3
};

await using var bus = new PostgresMessageBus(options, logger);

bus.Subscribe(new OrderConsumer());

await bus.Publish(new OrderSubmitted
{
    OrderId     = Guid.NewGuid(),
    ProductName = "Widget"
});
```

Multiple consumers can be subscribed to the same message type; each receives a copy of every message.

> **Worker Service / ASP.NET Core:** see the [Worker Services integration guide](WORKER_SERVICES.md) for complete `Program.cs` examples, scoped DI patterns, graceful shutdown, and Windows Service / systemd deployment.

### Message type names as channels

The message bus derives the channel name from `typeof(T).FullName`. Keep message type names under 500 characters (straightforward for any normal namespace depth).

---

## SQL Server Message Bus

`TownSuite.WorkQueues.SqlServer` provides a `SqlServerMessageBus` that mirrors
`PostgresMessageBus` but runs against SQL Server 2016+. It uses `UPDLOCK + ROWLOCK + READPAST`
table hints so that multiple concurrent consumer instances claim disjoint sets of messages
without blocking each other.

### Installation

```bash
dotnet add package TownSuite.WorkQueues.SqlServer
```

### Automatic migrations

`SqlServerMigrationHostedService` creates the `workqueue` table, filtered index, and stored
procedures on first startup. All DDL is idempotent — safe to run against an existing database.
Requires SQL Server 2016 or later (uses `CREATE OR ALTER PROCEDURE`).

```csharp
builder.Services.AddSingleton(new SqlServerTransportOptions
{
    ConnectionString      = "Server=.;Database=myapp;...",
    AdminConnectionString = "Server=.;Database=myapp;...",   // optional; falls back to ConnectionString
    Schema                = "dbo",
    MaxRetries            = 3
});

builder.Services.AddSqlServerMigrationHostedService();
```

### Wiring up the bus

```csharp
builder.Services.AddSqlServerMessageBus((sp, bus) =>
{
    bus.Subscribe(sp.GetRequiredService<OrderConsumer>());
});
builder.Services.AddTransient<OrderConsumer>();
builder.Services.AddHostedService<MessageBusHostedService>();
```

Consumer classes, the `MessageBusHostedService` wrapper, and publishing via `IMessageBus` are
identical to the PostgreSQL backend — see [Message Bus (publish/subscribe)](#message-bus-publishsubscribe)
and [WORKER_SERVICES.md](WORKER_SERVICES.md) for full examples.

### `SqlServerTransportOptions` reference

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | — | Used for message reads, writes, and publishing |
| `AdminConnectionString` | *(falls back to `ConnectionString`)* | Used by the migration service for DDL |
| `Schema` | `"dbo"` | Schema containing the workqueue table |
| `MaxBatchSize` | `100` (inherited) | Messages claimed per polling cycle |
| `MaxWaitTime` | `5s` (inherited) | Pause when the queue is empty |
| `MaxRetries` | `3` (inherited) | Attempts before dead-lettering |

---

## SQLite Backend (local development)

`TownSuite.WorkQueues.Sqlite` provides a `SqliteMessageBus` backed by a local SQLite file.
It is intended for **local development** where multiple processes on the same machine (for
example, a frontend that enqueues work and a separate worker that processes it) need to
communicate through a shared queue without running a database server.

> **Not for production.** SQLite serializes writes to a single file. For production workloads
> or multi-machine deployments, use the Postgres, SQL Server, or Redis backends.

### How claiming works

SQLite does not support `FOR UPDATE SKIP LOCKED`. Instead, the bus uses a `lockeduntil` /
`locktoken` column pair. A single atomic `UPDATE ... WHERE id IN (SELECT ... LIMIT n)` claims
a batch exclusively — SQLite's single-writer guarantee means only one process wins. Other
pollers see `lockeduntil` set to a future time and skip those rows.

If the claiming process crashes before completing, the message becomes available again once
`LockTimeout` elapses (default 60 s). This differs from the Postgres/SQL Server backends where
a transaction rollback makes the row immediately available.

WAL mode is enabled automatically by `SqliteMigrationHostedService` on first startup so that a
reader (e.g. your frontend enqueueing) and a writer (e.g. your worker processing) do not block
each other.

### Installation

```bash
dotnet add package TownSuite.WorkQueues.Sqlite
```

### Automatic migrations

`SqliteMigrationHostedService` creates the `workqueue` table, index, and enables WAL mode on
first startup. All DDL is idempotent — safe to run on every startup.

```csharp
builder.Services.AddSingleton(new SqliteTransportOptions
{
    ConnectionString = "Data Source=./workqueue.db",
    MaxRetries       = 3,
    LockTimeout      = TimeSpan.FromSeconds(60)   // default; tune to your slowest consumer
});

builder.Services.AddSqliteMigrationHostedService();
```

### Wiring up the bus

```csharp
builder.Services.AddSqliteMessageBus((sp, bus) =>
{
    bus.Subscribe(sp.GetRequiredService<OrderConsumer>());
});
builder.Services.AddTransient<OrderConsumer>();
builder.Services.AddHostedService<MessageBusHostedService>();
```

Consumer classes, the `MessageBusHostedService` wrapper, and publishing via `IMessageBus` are
identical to the other backends — see [Message Bus (publish/subscribe)](#message-bus-publishsubscribe)
and [WORKER_SERVICES.md](WORKER_SERVICES.md) for full examples.

### `SqliteTransportOptions` reference

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | — | SQLite connection string, e.g. `Data Source=./workqueue.db` |
| `LockTimeout` | `60s` | How long a claimed message is held before another process may reclaim it. Set above your slowest consumer's expected processing time. |
| `MaxBatchSize` | `100` (inherited) | Messages claimed per polling cycle |
| `MaxWaitTime` | `5s` (inherited) | Pause when the queue is empty |
| `MaxRetries` | `3` (inherited) | Attempts before dead-lettering |
| `RetryDelay` | `0` (inherited) | Minimum delay between retries |

### Inspecting the database

Because the queue is a plain SQLite file you can open it with any SQLite tool
(e.g. [DB Browser for SQLite](https://sqlitebrowser.org/)) to inspect pending, processed, and
dead-lettered messages:

```sql
-- pending messages
SELECT * FROM workqueue WHERE timeprocessedutc IS NULL AND failedat IS NULL ORDER BY timecreatedutc;

-- dead-lettered messages
SELECT * FROM workqueue WHERE failedat IS NOT NULL ORDER BY failedat DESC;

-- messages currently claimed by a worker
SELECT * FROM workqueue WHERE locktoken IS NOT NULL AND lockeduntil > datetime('now');
```

---

## Redis Backend

`TownSuite.WorkQueues.Redis` provides two Redis-backed implementations that do not require a database:

| Type | Interface | Backing structure |
|---|---|---|
| `RedisWorkQueue` | `IRedisWorkQueue` | Redis List (LPUSH / RPOP) |
| `RedisMessageBus` | `IMessageBus` | Redis Streams (XADD / XREADGROUP / XAUTOCLAIM) |

### Installation

```powershell
dotnet add package TownSuite.WorkQueues.Redis
```

### Redis work queue

```cs
using var mux = ConnectionMultiplexer.Connect("localhost:6379");
var queue = new RedisWorkQueue(mux, new RedisOptions { KeyPrefix = "myapp" });

// Enqueue
await queue.EnqueueAsync("orders", new OrderPayload { Id = 42 });

// Dequeue (returns null when empty)
var item = await queue.DequeueAsync<OrderPayload>("orders");
```

FIFO order is guaranteed per channel. There is no retry or dead-letter logic in `RedisWorkQueue` — use `RedisMessageBus` when you need those.

### Redis message bus

The same `IConsumer<T>` and `IMessageBus` contracts used by `PostgresMessageBus` apply here.

```cs
using var mux = ConnectionMultiplexer.Connect("localhost:6379");

var options = new RedisOptions
{
    KeyPrefix     = "myapp",
    ConsumerGroup = "workers",
    MaxBatchSize  = 50,
    MaxWaitTime   = TimeSpan.FromSeconds(2),
    MaxRetries    = 3
};

await using var bus = new RedisMessageBus(mux, options, logger);
bus.Subscribe(new OrderConsumer());

await bus.Publish(new OrderSubmitted { OrderId = Guid.NewGuid() });
```

#### How retry and dead-letter work

1. A message is claimed with `XREADGROUP`. On failure, it stays in the Pending Entry List.
2. Once idle for `ReclaimIdleTime` (default `MaxWaitTime × 3`), `XAUTOCLAIM` reclaims it and increments its retry counter.
   With a `RetryDelay` set, the failed message is instead moved to `{prefix}:stream:{type}:scheduled` and
   returns to the stream after the delay (with `RetryBackoffMultiplier` and `MaxRetryDelay`).
3. When the attempts reach `MaxRetries`, the message is copied to `{prefix}:stream:{type}:dead` and ACK-ed on the main stream.

Dead-lettered messages can be listed with `GetDeadLettered<T>`, replayed with `ReplayDeadLettered<T>`, or
inspected with any Redis client.

#### Scheduled delivery and transactional publish

`Publish(message, deliverAfter)` and `PublishOptions.DeliverAfter` hold the message in the
`{prefix}:stream:{type}:scheduled` sorted set until it is due. To publish inside your own Redis
`MULTI`/`EXEC`, pass the transaction; the message is added only if it executes:

```cs
var tran = mux.GetDatabase().CreateTransaction();
tran.AddCondition(Condition.KeyNotExists($"order:{orderId}:submitted"));
_ = tran.StringSetAsync($"order:{orderId}:submitted", "1");
Guid messageId = redisBus.Publish(new OrderSubmitted { OrderId = orderId }, tran);
await tran.ExecuteAsync();
```

### DI registration

```cs
builder.Services
    .AddRedisConnection("localhost:6379")
    .AddRedisMessageBus(opts =>
    {
        opts.KeyPrefix     = "myapp";
        opts.ConsumerGroup = "workers";
        opts.MaxRetries    = 3;
    });

// Optionally register a work queue on the same connection
builder.Services.AddRedisWorkQueue(opts => opts.KeyPrefix = "myapp");
```

`Subscribe` calls must be made before the application starts accepting traffic so the polling loop sees the handlers.

> **Worker Service / ASP.NET Core:** see the [Worker Services integration guide](WORKER_SERVICES.md) for complete Redis `Program.cs` examples, scoped DI, graceful shutdown, and deployment.

### `RedisOptions` reference

| Property | Default | Description |
|---|---|---|
| `KeyPrefix` | `"workqueue"` | Prefix for all Redis keys |
| `ConsumerGroup` | `"default"` | Stream consumer group name |
| `ConsumerName` | `{MachineName}-{ProcessId}` | Unique identity within the group. Includes the process ID so multiple processes on the same host maintain separate pending-entry lists. |
| `ReclaimIdleTime` | `MaxWaitTime × 3` | Idle threshold before a pending message is reclaimed |
| `MaxBatchSize` | `100` (inherited) | Messages read per polling cycle |
| `MaxWaitTime` | `5s` (inherited) | Pause when the stream is empty |
| `MaxRetries` | `3` (inherited) | Attempts before dead-lettering |

---

## Dead-Letter Queue & Retries

When a consumer throws, the message is **not** marked as processed. Instead:

1. `retrycount` is incremented for that row.
2. On the next polling cycle the message is picked up and retried.
3. Once `retrycount >= MaxRetries` (default `3`), the row's `failedat` column is set to the current timestamp and the message is permanently excluded from polling.

Dead-lettered rows (`failedat IS NOT NULL`) remain in the table for inspection and replay. No separate table is required.

### Inspecting and replaying via SQL

```sql
-- inspect failed messages
SELECT * FROM transport.workqueue WHERE failedat IS NOT NULL ORDER BY failedat DESC;

-- manually replay a single failed message (clears dead-letter state)
UPDATE transport.workqueue
SET failedat = NULL, retrycount = 0
WHERE id = 42;
```

### Programmatic replay via `ReplayDeadLettered<T>`

```csharp
// Resets failedat and retrycount for all dead-lettered messages of this type.
// Returns the number of messages queued for redelivery.
int replayed = await bus.ReplayDeadLettered<OrderSubmitted>();
```

For Redis, this reads entries from the `{prefix}:stream:{type}:dead` key and re-enqueues them to the main stream.

To replay one message, pass its id. `Fault<T>.MessageId` and `ConsumeContext<T>.MessageId` carry it, so an
admin endpoint or a support tool can replay exactly the message that failed:

```csharp
bool replayed = await bus.ReplayDeadLettered<PaymentConfirmed>(fault.MessageId);
```

It returns `false` when no dead-lettered message with that id exists on the channel.

### Listing dead-lettered messages

`GetDeadLettered<T>` lists dead-letters newest first, with the last error and whether the fault has
been delivered. Use it for an admin screen that shows what failed before someone replays it:

```csharp
IReadOnlyList<DeadLetteredMessage<PaymentConfirmed>> page =
    await bus.GetDeadLettered<PaymentConfirmed>(skip: 0, take: 50);

foreach (var dead in page)
    Console.WriteLine($"{dead.MessageId} {dead.FailedAt:u} {dead.ExceptionType}: {dead.ExceptionMessage}");
```

`Message` is `null` (default) if the stored payload no longer deserialises to `T`; `Payload` always
holds the raw JSON. Error details are recorded from this version on; older dead-letters list without them.

### Fault delivery guarantees

`Fault<T>` delivery is **at-least-once**, like message delivery:

1. The dead-letter is committed first, together with the error and a fault redelivery time.
2. The bus then delivers the fault to its `Fault<T>` consumers and records it as delivered.
3. If a fault consumer throws, or the process stops before step 2 finishes, the fault stays pending.
   After `FaultRedeliveryDelay` (default one minute) any bus on the same store with a fault consumer for
   `T` delivers it again, with `Fault<T>.IsRedelivery = true`.

So fault consumers must be idempotent. A bus that only calls `SubscribeFault<T>` (no `Subscribe<T>`)
still delivers faults for `T`.

Faults for messages dead-lettered while **no** bus had a fault consumer for `T` stay pending, and are
delivered once one subscribes. `QueueStatistics.PendingFaultCount` shows how many are waiting; a value
that stays above zero means a fault consumer keeps failing or none is subscribed.

### Retry backoff

`RetryDelay` holds a failed message back before its next attempt. Set `RetryBackoffMultiplier` to grow
the delay on each attempt, and `MaxRetryDelay` to cap it (without a cap, delays stop growing at one day):

```csharp
var options = new SqlServerTransportOptions
{
    MaxRetries             = 10,
    RetryDelay             = TimeSpan.FromSeconds(1),   // 1s, 2s, 4s, 8s … 5 min
    RetryBackoffMultiplier = 2.0,
    MaxRetryDelay          = TimeSpan.FromMinutes(5)
};
```

Backoff applies to every transport. On Redis, a message with no `RetryDelay` is retried after
`RedisOptions.ReclaimIdleTime` instead.

### Non-retryable exceptions

By default every consumer exception is retried. When some failures can never succeed (bad input, a
business rule), set `IsRetryable` to dead-letter them on the first failure instead of retrying:

```csharp
options.IsRetryable = ex => ex is not ValidationException;
```

The `Fault<T>` consumer still runs, with `Fault<T>.NonRetryable = true`. Prefer handling expected business
outcomes inside the consumer (record the result and return normally) and throw only for faults.

### Checking bus health with `IsPolling`

`IMessageBus.IsPolling` is `true` while the background polling loop is alive. Wire it into ASP.NET Core health checks to detect a silently-stopped bus:

```csharp
// In Program.cs
builder.Services.AddHealthChecks()
    .AddCheck("message-bus", () =>
    {
        var bus = app.Services.GetRequiredService<IMessageBus>();
        return bus.IsPolling
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Bus polling loop has stopped — no messages will be processed");
    });
```

`IsPolling` only says the loop is alive. To catch a channel nobody is consuming, alert on queue age
(see [Monitoring](#monitoring-metrics--queue-statistics)): a bus only claims messages for types it has
subscribed to, so messages published to a channel with no subscribed bus stay pending indefinitely.

### When fault consumers run

`Fault<T>` consumers run after the dead-letter state is committed, so a fault consumer that reads the
row (or replays it) sees `failedat` set. See [Fault delivery guarantees](#fault-delivery-guarantees).

---

## Message Expiry

Give a message an expiry when it is pointless to process it late — a cart hold the resident has long
since abandoned, for example. A message still undelivered when it expires is dead-lettered without
calling consumers, and its `Fault<T>` has `Expired = true` (exception type `MessageExpiredException`):

```csharp
await bus.Publish(new CartAddItemRequested { JobId = jobId },
    new PublishOptions { TimeToLive = TimeSpan.FromMinutes(10) });   // or ExpiresAt = …
```

`PublishOptions.DeliverAfter` schedules a message the same way. Both options work with the
transactional overload. Expiry is checked when a message is claimed; a message whose consumer is
already running is not interrupted. `ReplayDeadLettered` clears the expiry.

---

## Transactional Publish (outbox)

`Publish` normally opens its own connection. To publish only if your own database work commits, pass
your connection and transaction. The message and your rows commit or roll back together:

```csharp
await using var cn = new SqlConnection(connectionString);
await cn.OpenAsync();
await using var tx = cn.BeginTransaction();

await cn.ExecuteAsync("INSERT INTO PortalJob (JobId, Status) VALUES (@jobId, 'Queued')", new { jobId }, tx);
Guid messageId = await bus.Publish(new CartAddItemRequested { JobId = jobId }, cn, tx);

tx.Commit();
```

The connection must be to the database the bus polls (`SqlConnection`, `NpgsqlConnection` or
`SqliteConnection` to match the transport). Optional `PublishOptions` set a delivery time or expiry. It
returns the message id that consumers see as `ConsumeContext<T>.MessageId`. On Redis, pass an
`ITransaction` instead (see [Redis](#scheduled-delivery-and-transactional-publish)).

Use this in place of the legacy `IWorkQueue.Enqueue(channel, payload, cn, txn)`. It needs no stored
procedure and returns the message id.

---

## Concurrency

Each bus processes its claimed batch one message at a time. Set `MaxConcurrency` to run several polling
loops in one bus. Each loop claims its own batch, so at most `MaxConcurrency × MaxBatchSize` messages are
in flight:

```csharp
var options = new SqlServerTransportOptions
{
    MaxConcurrency = 4,   // up to 4 messages at once
    MaxBatchSize   = 1
};
```

Ordering is only guaranteed with `MaxConcurrency = 1`. Concurrency is per bus instance; several
processes or bus instances on the same database also share the work safely.

By default, on PostgreSQL and SQL Server the claimed batch is held in one open transaction (with row
locks) until every message in it is handled, and outcomes are committed together. With slow consumers
either keep `MaxBatchSize = 1`, or use lease-based claiming.

Your consumer's own database work runs on its own connection, separate from the claim. If the process
dies between your commit and the claim's, the message is redelivered, so consumers must be idempotent
(check `MessageId` or your own state first).

### Lease-based claiming

Set `ClaimLease` to hold claimed messages with a lease (`lockeduntil` / `locktoken` columns) instead of
an open transaction. No transaction or row lock stays open while consumers run, so a consumer that calls
a payment gateway does not hold up anything else, and batches can be larger:

```csharp
var options = new SqlServerTransportOptions
{
    ClaimLease     = TimeSpan.FromMinutes(2),  // longer than the slowest consumer
    MaxBatchSize   = 10,
    MaxConcurrency = 4
};
```

The trade-off is recovery time: if a process dies, its claimed messages become available again when
the lease expires, not immediately. A consumer that outlives its lease sees the message delivered a
second time, and its own late outcome is ignored (a warning is logged). SQLite always claims this way,
using `ClaimLease` when set and `LockTimeout` otherwise. Lease and transaction claimers can share a table.

---

## Purging Old Messages

Processed and dead-lettered messages stay in the store until deleted. Run a purge on a schedule:

```csharp
long processed = await bus.PurgeProcessed(DateTimeOffset.UtcNow.AddDays(-30));
long dead      = await bus.PurgeDeadLettered<PaymentConfirmed>(DateTimeOffset.UtcNow.AddDays(-90));
```

`PurgeProcessed` covers every channel; `PurgeDeadLettered<T>` one message type, including faults not yet
delivered for those messages. Both delete in batches so they can run against a busy queue. On Redis,
`PurgeProcessed` trims each stream no further than the oldest entry any consumer group still needs.

---

## Monitoring: Metrics & Queue Statistics

### Metrics

Every transport emits `System.Diagnostics.Metrics` instruments on the meter `TownSuite.WorkQueues`
(`WorkQueueMetrics.MeterName`), tagged with `messaging.system` and `messaging.destination.name` (the channel):

| Instrument | Type | Meaning |
|---|---|---|
| `townsuite.workqueues.messages.published` | counter | Messages published |
| `townsuite.workqueues.messages.processed` | counter | Successful deliveries |
| `townsuite.workqueues.messages.retried` | counter | Failed deliveries that will be retried |
| `townsuite.workqueues.messages.deadlettered` | counter | Messages dead-lettered |
| `townsuite.workqueues.messages.expired` | counter | Messages dead-lettered because they expired |
| `townsuite.workqueues.message.duration` | histogram (s) | Consumer time per attempt |
| `townsuite.workqueues.queue.pending` | gauge | Pending messages, for tracked queues |
| `townsuite.workqueues.queue.deadlettered` | gauge | Dead-lettered messages, for tracked queues |
| `townsuite.workqueues.queue.pending_faults` | gauge | Undelivered faults, for tracked queues |
| `townsuite.workqueues.queue.oldest_ready_age` | gauge (s) | Age of the oldest deliverable message, for tracked queues |

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(WorkQueueMetrics.MeterName).AddPrometheusExporter());
```

### Queue statistics

Backlog size and age need a query, so they are read on demand:

```csharp
QueueStatistics stats = await bus.GetQueueStatistics<CartAddItemRequested>();
// stats.PendingCount       — not yet processed (includes scheduled and waiting-for-retry)
// stats.DeadLetteredCount  — dead-lettered and held for replay
// stats.PendingFaultCount  — dead-letters whose Fault<T> has not been delivered yet
// stats.OldestReadyAge     — how long the oldest deliverable message has waited (null if none)
```

Alert when `OldestReadyAge` grows past what the channel should tolerate: it catches slow consumers,
stopped workers and channels with no subscriber. Messages scheduled for the future are not counted
towards the age until they become due.

To publish these as the `queue.*` gauges, track the queue. Statistics are read in the background, so
metric collection never queries the store:

```csharp
await using var tracking = WorkQueueMetrics.TrackQueue<CartAddItemRequested>(bus, TimeSpan.FromSeconds(30));
```

### Health checks

`TownSuite.WorkQueues.HealthChecks` adds a health check that is `Unhealthy` when the bus's polling has
stopped and `Degraded` (configurable) when a queue threshold is exceeded. Statistics are included in the
result data:

```csharp
builder.Services.AddHealthChecks().AddMessageBus(configure: o => o
    .Queue<CartAddItemRequested>(q => { q.MaxOldestReadyAge = TimeSpan.FromSeconds(30); q.MaxPendingFaults = 0; })
    .Queue<PaymentConfirmed>(q => q.MaxDeadLettered = 0));
```

Pass `busFactory` to check a bus that is not the DI-registered `IMessageBus` (for example one per tenant),
and register one check per bus with distinct names.

---

## Configuration Reference

### `BatchOptions`

| Property | Default | Description |
|---|---|---|
| `MaxBatchSize` | `100` | Maximum messages claimed per polling cycle |
| `MaxWaitTime` | `5s` | How long to pause when the queue is empty before polling again |
| `ContinuousPolling` | `false` | When `true`, skips the `MaxWaitTime` delay between empty polls. Use only in tests or latency-critical scenarios — otherwise leaves CPU and database idle time on the table. |
| `MaxRetries` | `3` | Delivery attempts before a message is dead-lettered |
| `RetryDelay` | `0` | Delay before a failed message is retried (SQL transports) |
| `RetryBackoffMultiplier` | `1.0` | Multiplier applied to `RetryDelay` for each further attempt |
| `MaxRetryDelay` | `null` (1 day) | Cap on the computed retry delay |
| `MaxConcurrency` | `1` | Polling loops run in parallel by one bus |
| `IsRetryable` | `null` (retry all) | Return `false` to dead-letter an exception immediately |
| `ClaimLease` | `null` (transaction) | PostgreSQL / SQL Server: hold claims with a lease of this length instead of an open transaction |
| `FaultRedeliveryDelay` | `1 min` | Delay before an undelivered `Fault<T>` is delivered again; also the lease a bus holds while redelivering |

### `SqlTransportOptions` (extends `BatchOptions`)

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | — | Connection string used for message reads and writes |
| `AdminConnectionString` | — | Connection string used for running migrations (may need DDL permissions) |
| `Schema` | `"transport"` | PostgreSQL schema that contains the workqueue table |

---

## Running the Tests

Tests use [Testcontainers](https://dotnet.testcontainers.org/) and spin up real PostgreSQL and SQL Server instances automatically. The only prerequisite is a running Docker daemon.

```bash
cd TownSuite.WorkQueues.Testing
dotnet test                      # both target frameworks (net8.0 and net10.0)
dotnet test -f net10.0           # one framework
```

Running the `net8.0` tests needs the .NET 8 runtime installed alongside the SDK.

No external database setup or `appsettings.json` changes are needed.

---

## Upgrading from Earlier Versions

### Serialization change

`DbBackedWorkQueue` now writes payloads with `System.Text.Json` (compact, no `$type`):

```json
{"Id":42}
```

Old payloads written by Newtonsoft.Json `TypeNameHandling.All` look like:

```json
{"$type":"MyApp.OrderPayload, MyApp","Id":42}
```

**No drain is required for the common case.** The library reads both formats:

- `$type` annotations on POCOs and nested objects are silently ignored.
- Collection roots wrapped in `{"$type":"...","$values":[...]}` are unwrapped automatically.

The one scenario that still requires draining (or manual replay) is **polymorphic payloads**
where a derived type was stored and the call site deserialises to an abstract base type — an
unusual pattern. All other callers can upgrade in-place.

### Schema changes

The following DDL changes are applied automatically by `PostgresMigrationHostedService` on first startup after upgrade. For SQL Server, apply the corresponding alterations manually.

| Change | Detail |
|---|---|
| `channel` column widened | `VARCHAR(50)` → `VARCHAR(500)` |
| `retrycount` column added | `INT NOT NULL DEFAULT 0` |
| `failedat` column added | `TIMESTAMP NULL` |
| Partial index added | On `(channel, timecreatedutc)` where unprocessed and not failed |

The migration statements are idempotent (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`, `DO` block guards) and safe to run on an existing table with live data.

---

## Benchmarks

These numbers are from a single client machine talking to a dedicated database host. The library is designed for **sustained throughput below 10,000 calls/second**.

### Message bus consumption

`MessageBusThroughputBenchmarks` (run with `dotnet test --filter "Category=Benchmark"`) consumes 400
messages with a consumer that spends 20 ms per message (standing in for real I/O), against
Testcontainers databases on one Apple Silicon laptop (Docker Desktop), 26.1.0, .NET 10:

| Configuration | PostgreSQL | SQL Server |
|---|---|---|
| `MaxConcurrency = 1`, `MaxBatchSize = 1` | 35 msg/s | 32 msg/s |
| `MaxConcurrency = 4`, `MaxBatchSize = 1` | 135 msg/s | 140 msg/s |
| `MaxConcurrency = 8`, `MaxBatchSize = 1` | 305 msg/s | 264 msg/s |
| `MaxConcurrency = 1`, `MaxBatchSize = 10` | 40 msg/s | 35 msg/s |
| `MaxConcurrency = 4`, `MaxBatchSize = 10`, `ClaimLease` | 151 msg/s | 132 msg/s |
| `MaxConcurrency = 8`, `MaxBatchSize = 10`, `ClaimLease` | 307 msg/s | 302 msg/s |
| 200 undelivered faults redelivered by a second bus | 2.2 s | 2.2 s |

With a consumer doing real work, throughput is bounded by the consumer, so it scales with
`MaxConcurrency`; a larger batch alone does not help, because a loop handles its batch one message at a
time. Lease claiming costs nothing measurable over transaction claiming and holds no locks.

### PostgreSQL

| Threads | Calls/1 s | Calls/30 s |
|---|---|---|
| 1 | 1,953 | 58,590 |
| 10 | 11,509 | 345,278 |
| 20 | 16,661 | 499,844 |
| 30 | 18,062 | 541,895 |
| 40 | 18,366 | 551,004 |
| 50 | 19,090 | 572,727 |

```ini
BenchmarkDotNet=v0.13.5, OS=macOS Ventura 13.4.1 [Darwin 22.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK=6.0.408 / .NET 6.0.16, Arm64 RyuJIT AdvSIMD
```

| Method | Mean | Error | StdDev |
|---|---|---|---|
| Enqueue | 519.1 µs | 15.16 µs | 44.47 µs |

### SQL Server

| Threads | Calls/1 s | Calls/30 s |
|---|---|---|
| 1 | 1,241 | 37,228 |
| 10 | 12,606 | 378,272 |
| 20 | 12,943 | 388,693 |
| 30 | 13,135 | 394,284 |
| 40 | 13,210 | 396,515 |
| 50 | 12,944 | 388,545 |

```ini
BenchmarkDotNet=v0.13.5, OS=macOS Ventura 13.4.1 [Darwin 22.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK=6.0.408 / .NET 6.0.16, Arm64 RyuJIT AdvSIMD
```

| Method | Mean | Error | StdDev |
|---|---|---|---|
| Enqueue | 794.8 µs | 12.84 µs | 12.01 µs |
