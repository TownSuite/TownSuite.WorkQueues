# Migrating from Direct WorkQueue to the Message Bus

This document explains how to move from the pull-based `IWorkQueue` / `DbBackedWorkQueue`
pattern to the push-based `IMessageBus` / `PostgresMessageBus` pattern introduced in v2.

It is written for both humans and AI coding assistants.

---

## Decision checklist — should you migrate?

Answer these questions before starting:

| Question | If yes | If no |
|---|---|---|
| Is your database PostgreSQL or SQL Server? | Continue | **Do not migrate** — Redis is the only other bus-capable backend. Keep `IWorkQueue` or switch to Redis. |
| Are you on v2.0.0 or later? | Continue | Upgrade to v2 first (see CHANGELOG). |
| Do you need to inspect or replay failed messages? | The bus dead-letters automatically after `MaxRetries`. Continue. | Either works. |
| Do you need to **publish** only if your own business transaction commits? | Use the transactional `Publish(message, cn, txn)` overload (see [Transactional publishing](#transactional-publishing)). Continue. | Continue. |
| Must the **consumer's** work commit in the same transaction as the dequeue itself (exactly-once)? | **Keep `IWorkQueue`** — the bus claims messages in its own transaction; consumers must be idempotent. | Continue. |
| Are you happy for the channel name to be the message type's fully-qualified name? | Continue | You can still migrate, but read the **channel name** section carefully. |

---

## Conceptual differences

### Pull vs. push

```
Direct WorkQueue (pull)                Message Bus (push)
──────────────────────────────────     ──────────────────────────────────
Your code: loop, dequeue, process      Your code: Consume() method only
Library:   stores the message          Library:   stores + dispatches
You own:   the polling loop            Library owns: the polling loop
You own:   transaction, retry logic    Library owns: retry, dead-letter
```

### Channel naming

This is the most important difference for in-flight migration.

| API | Channel value |
|---|---|
| `IWorkQueue.Enqueue("orders", ...)` | `"orders"` — whatever string you pass |
| `IMessageBus.Publish<OrderPayload>(...)` | `typeof(OrderPayload).FullName` — e.g. `"MyApp.Models.OrderPayload"` |

Messages already stored in the database under the old channel name (`"orders"`) will **not** be
picked up by a bus subscriber registered for `OrderPayload`, because the bus polls for
`"MyApp.Models.OrderPayload"`.

See [Handling in-flight messages](#handling-in-flight-messages) below.

### Transaction ownership

```csharp
// Direct WorkQueue — you own the transaction
using var txn = cn.BeginTransaction();
var item = await workQueue.Dequeue<Order>(channel, cn, txn);
DoWork(item);
txn.Commit();   // or txn.Rollback() — your decision

// Message Bus — the bus owns the transaction
public class OrderConsumer : IConsumer<Order>
{
    public async Task Consume(ConsumeContext<Order> context)
    {
        DoWork(context.Message);
        // No transaction to manage. If this method throws, the
        // message is retried up to MaxRetries times, then dead-lettered.
    }
}
```

---

## Step-by-step migration

### Step 1 — Identify code to migrate

Search the codebase for:

```
IWorkQueue
DbBackedWorkQueue
DbBackedWorkQueue_NonDestructive
.Enqueue(
.Dequeue<
```

For each call site, note:
- The **channel string** (first argument to `Enqueue` / `Dequeue`)
- The **payload type** `T`
- Whether the dequeue is inside a hosted service / background worker

### Step 2 — Create a message type (if you don't have one)

The bus derives the channel from `typeof(T).FullName`. If your existing code passes an
anonymous object or a generic dictionary, create a dedicated class:

```csharp
// Before — anonymous type, no stable channel
await workQueue.Enqueue("orders", new { OrderId = 42, Customer = "Alice" }, cn);

// After — named type, stable channel = "MyApp.Models.OrderPayload"
public class OrderPayload
{
    public int OrderId { get; set; }
    public string Customer { get; set; } = string.Empty;
}
```

Keep the class name and namespace stable. Renaming or moving it changes the channel and breaks
any messages currently in the database for that type.

### Step 3 — Replace the polling worker with IConsumer<T>

Take the processing logic from inside the dequeue loop and put it in a consumer class.

**Before:**

```csharp
public class OrderWorker : BackgroundService
{
    private readonly IWorkQueue _queue;
    private readonly IDbConnectionFactory _db;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var cn = _db.CreateConnection();
            await cn.OpenAsync(ct);

            using var txn = cn.BeginTransaction();
            var order = await _queue.Dequeue<OrderPayload>("orders", cn, txn);

            if (order is null)
            {
                txn.Rollback();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                await ProcessOrder(order);
                txn.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process order");
                txn.Rollback();
            }
        }
    }
}
```

**After:**

```csharp
public class OrderConsumer : IConsumer<OrderPayload>
{
    public async Task Consume(ConsumeContext<OrderPayload> context)
    {
        await ProcessOrder(context.Message);
        // Throw to trigger retry; the bus handles back-off and dead-lettering.
    }
}
```

Delete `OrderWorker` entirely — the bus provides the loop.

### Step 4 — Replace Enqueue with Publish

```csharp
// Before
await _workQueue.Enqueue("orders", payload, cn, txn);

// After
await _bus.Publish(payload);
```

`Publish` opens its own connection internally. Remove the `cn` / `txn` arguments.
If the `Enqueue` was inside a business transaction you want to keep atomic, use
`await _bus.Publish(payload, cn, txn)` instead — see [Transactional publishing](#transactional-publishing) below.

### Step 5 — Wire up DI

```csharp
// Program.cs / Startup.cs
builder.Services.AddSingleton(new SqlTransportOptions
{
    ConnectionString      = connectionString,
    AdminConnectionString = connectionString,
    Schema                = "transport",   // or "public"
    MaxRetries            = 3
});

// Creates the schema/table/index on first startup
builder.Services.AddPostgresMigrationHostedService();

// Register the bus as a singleton so it lives for the app lifetime
builder.Services.AddSingleton<IMessageBus>(sp =>
{
    var options = sp.GetRequiredService<SqlTransportOptions>();
    var logger  = sp.GetRequiredService<ILogger<PostgresMessageBus>>();
    var bus     = new PostgresMessageBus(options, logger);
    bus.Subscribe(new OrderConsumer());
    return bus;
});
```

Replace `IWorkQueue` injection points with `IMessageBus`.

### Step 6 — Remove the background worker registration

```csharp
// Remove this
builder.Services.AddHostedService<OrderWorker>();
```

### Step 7 — Drain in-flight messages before cutover

Because the channel name changes (from your old string to `typeof(T).FullName`), messages
already in the database won't be processed by the new bus. Before switching over:

```sql
-- Find messages still queued under the old channel name
SELECT count(*) FROM transport.workqueue
WHERE channel = 'orders'
  AND timeprocessedutc IS NULL
  AND failedat IS NULL;
```

Options:
- **Wait until the queue is empty** under the old name before deploying (cleanest).
- **Re-enqueue** old messages under the new channel name (see below).
- **Keep the old worker running in parallel** until the old channel drains, then remove it.

```sql
-- Re-enqueue under the new type name (adjust schema/channel names)
UPDATE transport.workqueue
SET channel = 'MyApp.Models.OrderPayload'
WHERE channel = 'orders'
  AND timeprocessedutc IS NULL
  AND failedat IS NULL;
```

---

## Handling in-flight messages

This is the most common migration pitfall. The table below summarises options:

| Strategy | How | When to use |
|---|---|---|
| Drain then deploy | Wait for old channel to empty, then deploy new code | Low-traffic queues |
| Parallel cutover | Run old worker + new bus simultaneously; old worker drains old rows, new bus handles new rows | Cannot afford downtime |
| SQL rename | `UPDATE ... SET channel = 'FullTypeName' WHERE channel = 'old-name'` | Fast queues with few in-flight rows |
| Keep old channel | Don't migrate the consumer; only migrate the producer | Gradual rollout |

---

> `IWorkQueue` and `DbBackedWorkQueue` are now `[Obsolete]` (diagnostic `TSWQ001`). They keep working;
> suppress the warning with `<NoWarn>$(NoWarn);TSWQ001</NoWarn>` until a project is migrated.

## Transactional publishing

To publish only if your business transaction commits, pass your connection and transaction to
`Publish`. The message row is inserted on your transaction, so it commits or rolls back with your
own writes (the transactional outbox pattern):

```csharp
// Inside your business transaction
await cn.ExecuteAsync("UPDATE Orders SET Status = 'Submitted' WHERE Id = @id", new { id }, txn);
Guid messageId = await bus.Publish(new OrderSubmitted { OrderId = id }, cn, txn);
txn.Commit();
```

The connection must be to the database the bus polls, of the transport's own type (`NpgsqlConnection`,
`SqlConnection` or `SqliteConnection`). Pass `PublishOptions` to schedule the message or give it an
expiry. The returned id is the `ConsumeContext<T>.MessageId` consumers see. On Redis, pass an
`ITransaction` (`redisBus.Publish(message, transaction)`) to publish inside your own `MULTI`/`EXEC`.

> Older versions recommended `IWorkQueue.Enqueue(typeof(T).FullName, payload, cn, txn)` for this.
> That still works, but it needs the `workqueue_enqueue` stored procedure and does not return the
> message id. Prefer the `Publish` overload.

---

## Dead-letter management after migration

Failed messages appear in the database with `failedat IS NOT NULL`.

```sql
-- Inspect dead-lettered messages
SELECT id, channel, retrycount, failedat, payload
FROM transport.workqueue
WHERE failedat IS NOT NULL
ORDER BY failedat DESC;

-- Replay a single message (clears dead-letter state)
UPDATE transport.workqueue
SET failedat = NULL, retrycount = 0
WHERE id = 42;

-- Replay all failed messages for one message type
UPDATE transport.workqueue
SET failedat = NULL, retrycount = 0
WHERE channel = 'MyApp.Models.OrderPayload'
  AND failedat IS NOT NULL;
```

---

## Side-by-side API reference

| Concern | Direct WorkQueue | Message Bus |
|---|---|---|
| Enqueue / publish | `workQueue.Enqueue("channel", obj, cn)` | `bus.Publish(obj)`, or `bus.Publish(obj, cn, txn)` inside your transaction |
| Dequeue / consume | `workQueue.Dequeue<T>("channel", cn, txn)` | `IConsumer<T>.Consume(ctx)` |
| Channel name | Arbitrary string you supply | `typeof(T).FullName` (auto) |
| Polling loop | You write it (BackgroundService) | Built into the bus |
| Transaction | You open and commit | Bus-managed |
| Retry on failure | Increment `offset`, loop again | Automatic, up to `MaxRetries`, with optional backoff |
| Dead-letter | No built-in; you decide what to do | `failedat` column, queryable |
| Database | PostgreSQL and SQL Server | PostgreSQL, SQL Server, Redis, SQLite |
| Multiple consumers | Not applicable (single dequeuer per item) | Multiple `Subscribe` calls |
| Ordered delivery | Yes (dequeue by id ascending) | Yes with `MaxConcurrency = 1` (poll by timecreatedutc ascending) |
| Concurrency | One dequeue per call; run more workers | `MaxConcurrency` polling loops per bus |
| Monitoring | Query the table yourself | `WorkQueueMetrics` meter + `GetQueueStatistics<T>()` |

---

## What NOT to migrate

Keep `IWorkQueue` / `DbBackedWorkQueue` when:
- Your processing logic must run **inside the same transaction** as other database writes (e.g. dequeue and update a related record atomically).
- You use the **offset parameter** to build a specific sharding or skip strategy across multiple worker instances.
- You use **`DbBackedWorkQueue_NonDestructive`** and rely on re-reading processed rows for audit or replay — the message bus marks rows processed and excludes them from future polls.
- You need **exactly-once** semantics backed by a database constraint in your own schema.

---

## Checklist for AI coding assistants

When asked to perform this migration on a codebase, follow this sequence:

1. **Grep for `IWorkQueue`, `.Enqueue(`, `.Dequeue<`** to find all call sites.
2. **For each channel string**, find the payload type `T` used in the corresponding `Dequeue<T>` call.
   - If `T` is `dynamic`, `object`, or an anonymous type, create a named DTO first.
   - Record the mapping: old channel string → `typeof(T).FullName`.
3. **Check the database** (ask the user or read config) — PostgreSQL or SQL Server? Continue. Redis? Use `RedisMessageBus` instead of `PostgresMessageBus`/`SqlServerMessageBus` and skip the migration DDL step.
4. **Create `IConsumer<T>` classes** — move the body of the dequeue loop into `Consume()`.
   - Remove transaction management, null-check loops, and offset logic from the body.
   - If the old code incremented offset on failure, remove it — the bus retries automatically.
5. **Replace `Enqueue` calls with `Publish`** — remove `cn` and `txn` arguments, unless the enqueue was inside a business transaction; then use `Publish(payload, cn, txn)`.
6. **Wire DI** — register `SqlTransportOptions`, `AddPostgresMigrationHostedService`, and `IMessageBus`.
7. **Delete the background workers** that contained the dequeue loops.
8. **Ask the user about in-flight messages** before finalising — do they need a drain strategy?
9. **Do not migrate** if the dequeue was inside a business transaction that wrote to other tables in the same commit. Flag this to the user and keep `IWorkQueue` for that site. (Enqueues inside a business transaction *can* migrate, via `Publish(payload, cn, txn)`.)
10. **Verify** the channel string written by any remaining `IWorkQueue.Enqueue` calls, if used as a transactional outbox, exactly matches `typeof(T).FullName` of the target consumer.
