# Changelog

All notable changes to this project will be documented in this file.

---

## [Unreleased]

### New features

- **Retry backoff** — `BatchOptions.RetryBackoffMultiplier` (default `1.0`) grows `RetryDelay` on each
  attempt; `BatchOptions.MaxRetryDelay` caps it (default one day). PostgreSQL, SQL Server and SQLite.
  ```csharp
  options.RetryDelay = TimeSpan.FromSeconds(1);
  options.RetryBackoffMultiplier = 2.0;              // 1s, 2s, 4s, 8s …
  options.MaxRetryDelay = TimeSpan.FromMinutes(5);
  ```
- **Non-retryable exceptions** — `BatchOptions.IsRetryable` (`Func<Exception, bool>`). Returning `false`
  dead-letters the message on its first failure; `Fault<T>.NonRetryable` is `true`. All transports.
- **Replay one dead-lettered message** — `IMessageBus.ReplayDeadLettered<T>(Guid messageId)`.
  `Fault<T>.MessageId` carries the id. All transports.
- **Transactional publish (outbox)** — `IMessageBus.Publish<T>(message, DbConnection, DbTransaction?, deliverAfter?)`
  inserts on the caller's transaction and returns the message id. PostgreSQL, SQL Server and SQLite.
  Replaces using `IWorkQueue.Enqueue` as an outbox.
- **Bounded concurrency** — `BatchOptions.MaxConcurrency` (default `1`) runs that many polling loops
  in one bus. `IsPolling` is `true` only while every loop is running. All transports.
- **Metrics** — every transport emits counters for published, processed, retried and dead-lettered
  messages and a consumer duration histogram on the `TownSuite.WorkQueues` meter (`WorkQueueMetrics`).
- **Queue statistics** — `IMessageBus.GetQueueStatistics<T>()` returns pending and dead-lettered
  counts and the age of the oldest deliverable message, for health checks and alerting. All transports.

### Fixes

- **`Fault<T>` consumers ran before the dead-letter was committed** (PostgreSQL, SQL Server). They now
  run after the claim transaction commits, so a failed commit no longer reports a dead-letter that
  did not happen, and a fault consumer that reads the row no longer blocks on its lock.

### Schema changes

A filtered index over dead-lettered rows speeds up replay and statistics. The migration hosted
services add it automatically; for manually managed schemas:

| Database | DDL |
|---|---|
| SQL Server | `CREATE NONCLUSTERED INDEX IX_workqueue_channel_deadlettered ON dbo.workqueue (channel, messageid) WHERE failedat IS NOT NULL` |
| PostgreSQL | `CREATE INDEX IF NOT EXISTS ix_workqueue_channel_deadlettered ON workqueue (channel, messageid) WHERE failedat IS NOT NULL` |
| SQLite | `CREATE INDEX IF NOT EXISTS IX_workqueue_channel_deadlettered ON workqueue (channel, messageid) WHERE failedat IS NOT NULL` |

### Behaviour notes

- `Publish` now writes `messageid` explicitly instead of relying on the column default.
- New `IMessageBus` members have default implementations that throw `NotSupportedException`, so
  third-party implementations keep compiling.

---

## [2.5.0] — 2026-06-14

### New features

- **SQLite backend** (`TownSuite.WorkQueues.Sqlite`) — A new `SqliteMessageBus` backed by a
  local SQLite file. Designed for local development scenarios where multiple processes on the
  same machine (e.g. a frontend that enqueues and a separate worker that dequeues) need to
  share a queue without running a database server. WAL mode is enabled automatically so readers
  and writers do not block each other. Claiming is emulated with a `lockeduntil` / `locktoken`
  column pair — SQLite's single-writer serialization makes this race-free across processes.
  Supports all `IMessageBus` features: at-least-once delivery, retry, retry delay, scheduled
  delivery, dead-lettering, `Fault<T>` consumers, `ReplayDeadLettered<T>`, and scoped consumers.
  See [SQLite Backend (local development)](Readme.md#sqlite-backend-local-development) for setup
  and a note on the `LockTimeout` difference vs. the transaction-based backends.

---

## [2.4.0] — 2026-06-13

### New features

- **Scoped consumers** (`bus.Subscribe<TMessage, TConsumer>()`) — Resolves a fresh `IConsumer<TMessage>` instance from a new `IServiceScope` for each message dispatch. Ideal for consumers that depend on scoped services (e.g. EF Core `DbContext`). Requires `IServiceProvider` to be passed to the bus constructor; the DI extensions (`AddPostgresMessageBus`, `AddSqlServerMessageBus`, `AddRedisMessageBus`) now supply it automatically:
  ```csharp
  // Registration:
  services.AddScoped<OrderConsumer>();
  services.AddPostgresMessageBus((sp, bus) =>
  {
      bus.Subscribe<OrderSubmitted, OrderConsumer>();
  });
  ```

- **`Fault<T>` dead-letter hook consumer** — When a message exhausts all retries and is dead-lettered, any registered `IConsumer<Fault<T>>` receives a `Fault<T>` notification in-process:
  ```csharp
  bus.SubscribeFault(new DeadLetterAlerter<OrderSubmitted>());
  
  class DeadLetterAlerter<T> : IConsumer<Fault<T>>
  {
      public Task Consume(ConsumeContext<Fault<T>> ctx)
      {
          Console.WriteLine($"Dead-lettered: {ctx.Message.ExceptionMessage}");
          return Task.CompletedTask;
      }
  }
  ```
  `Fault<T>` carries: `OriginalMessage`, `ExceptionType`, `ExceptionMessage`, `StackTrace` (null for Redis), `FaultedAt`, `AttemptCount`.

- **Scheduled / delayed delivery** — `IMessageBus` gains an overload `Publish<T>(T message, DateTimeOffset deliverAfter, CancellationToken ct = default)`. Messages are withheld from polling until the scheduled time via a new `scheduledfor` column. Supported by Postgres and SQL Server; throws `NotSupportedException` on Redis:
  ```csharp
  await bus.Publish(message, deliverAfter: DateTimeOffset.UtcNow.AddHours(2));
  ```

- **Retry delay / backoff** (`BatchOptions.RetryDelay`) — New `TimeSpan` property (default `TimeSpan.Zero`). When non-zero, a failed message's `scheduledfor` column is set to `NOW() + RetryDelay`, so it is withheld from the next polling cycle for the configured duration. Applies to Postgres and SQL Server; Redis transports use `ReclaimIdleTime` for the same effect:
  ```csharp
  options.RetryDelay = TimeSpan.FromSeconds(30);  // 30-second back-off between retries
  ```

- **`MessageId` + `SentTime` on `ConsumeContext<T>`** — New default interface members `Guid MessageId` and `DateTimeOffset SentTime`. All three buses populate these from the backing store. Use `MessageId` for idempotency checks:
  ```csharp
  public async Task Consume(ConsumeContext<OrderSubmitted> ctx)
  {
      if (await _idempotency.IsProcessed(ctx.MessageId)) return;
      // ...
  }
  ```
  Requires schema migrations: a `messageid` column (`UUID`/`UNIQUEIDENTIFIER`, default `gen_random_uuid()`/`NEWID()`) and a `scheduledfor` column (`TIMESTAMP`/`DATETIME`) are added to the `workqueue` table (idempotent `ADD COLUMN IF NOT EXISTS` / `IF NOT EXISTS` guards).

### Schema migration notes

`PostgresMigrationHostedService` and `SqlServerMigrationHostedService` apply all changes automatically on startup. For manually managed schemas, run the updated scripts in `scripts/`.

| Change | Postgres | SQL Server |
|---|---|---|
| New `scheduledfor` column | `ADD COLUMN IF NOT EXISTS scheduledfor TIMESTAMP NULL` | `IF NOT EXISTS ... ADD [scheduledfor] DATETIME NULL` |
| New `messageid` column | `ADD COLUMN IF NOT EXISTS messageid UUID NOT NULL DEFAULT gen_random_uuid()` | `IF NOT EXISTS ... ADD [messageid] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() WITH VALUES` |

Both changes are idempotent and non-destructive against existing data.

### DI extension updates

- `AddPostgresMessageBus`, `AddSqlServerMessageBus`, and `AddRedisMessageBus` now pass the root `IServiceProvider` to the bus constructor automatically, enabling `bus.Subscribe<TMessage, TConsumer>()` inside the configure callback without any additional wiring.

---

## [2.3.0] — 2026-06-12

### Bug fixes

- **Duplicate `Subscribe` calls no longer cause double-dispatch** — Handler storage changed from `ConcurrentBag<Func>` to `ConcurrentDictionary<object, Func>` keyed on the consumer instance. Calling `bus.Subscribe(consumer)` twice silently no-ops instead of registering the same handler twice and delivering every message twice.

- **`AllowEmptyBatches` was not honoured** — The between-poll `Task.Delay` was always executed regardless of the option value. Fixed by extracting a `WaitAsync()` helper in each bus that checks the flag before delaying.

- **`LegacyJsonDeserializer` `$values` false positives** — The fallback that unwrapped `$values` arrays could trigger on malformed payloads that contained `$values` without `$type`. The guard now requires both fields to be present before attempting the fallback.

### New features

- **`IMessageBus.IsPolling`** — New read-only property on the interface and all three bus implementations. Returns `true` while the background polling loop is alive and `false` if it has stopped unexpectedly. Use this to implement health checks:
  ```csharp
  builder.Services.AddHealthChecks()
      .AddCheck("message-bus", () =>
          sp.GetRequiredService<IMessageBus>().IsPolling
              ? HealthCheckResult.Healthy()
              : HealthCheckResult.Unhealthy("Bus polling loop has stopped"));
  ```

- **`IMessageBus.ReplayDeadLettered<T>()`** — Resets `failedat` and `retrycount` for all dead-lettered messages of a given type so they are redelivered on the next polling cycle. Returns the count of rows (or stream entries for Redis) that were replayed:
  ```csharp
  int replayed = await bus.ReplayDeadLettered<OrderSubmitted>();
  ```
  The Redis implementation reads the `{stream}:dead` key and re-enqueues each entry to the main stream.

- **`ConsumeContext<T>.CancellationToken`** — New default interface property (returns `CancellationToken.None` for custom implementations). All three bus backends now pass their shutdown `CancellationToken` to consumers via `SimpleConsumeContext<T>`, so consumers doing async I/O can observe graceful shutdown:
  ```csharp
  public async Task Consume(ConsumeContext<OrderSubmitted> context)
  {
      await _db.SaveAsync(context.Message, context.CancellationToken);
  }
  ```

- **`IRedisWorkQueue` gains `CancellationToken` parameters** — `EnqueueAsync` and `DequeueAsync` now accept an optional `CancellationToken cancellationToken = default`. No breaking change — existing callers compile unchanged.

- **`BatchOptions.ContinuousPolling`** renames `AllowEmptyBatches`. The old name is kept as an `[Obsolete]` computed alias that redirects to `ContinuousPolling`. The new name accurately describes the behaviour: "poll again immediately without waiting `MaxWaitTime` when the last batch was empty."

### Production readiness

- **`Task.Yield()` in bus constructors** — Ensures `Subscribe()` calls made immediately after `new PostgresMessageBus(...)` are registered before the first polling cycle executes, eliminating a rare race where the loop could fire before any handlers were registered.

- **`RedisOptions.ConsumerName` default includes `ProcessId`** — Default changed from `Environment.MachineName` to `$"{MachineName}-{ProcessId}"`. Multiple processes on the same host no longer share a pending-entry list, which previously caused incorrect retry counts and missed reclaims.

- **`AdminConnectionString` fallback** — `SqlTransportOptions` (Postgres) and `SqlServerTransportOptions` now fall back to `ConnectionString` when `AdminConnectionString` is not set. Migration services no longer require the admin string to be specified separately.

- **`AddPostgresMessageBus` DI extension added** — `PostgresMigrationHostedServiceExtensions` now exposes `AddPostgresMessageBus(Action<IServiceProvider, PostgresMessageBus>)`, matching the pattern already present in the SQL Server package.

- **`AddRedisMessageBus` optional `subscribe` callback** — The DI helper now accepts an optional `Action<IServiceProvider, RedisMessageBus>? subscribe` parameter for registering consumers in the factory, matching the Postgres/SqlServer pattern.

### Documentation

- XML documentation added to `MessageDto`, `DbBackedWorkQueue`, `DbBackedWorkQueue_NonDestructive`, and `BatchOptions` classes (previously undocumented).
- README updated: `ContinuousPolling` replaces `AllowEmptyBatches` in config table; `ReplayDeadLettered<T>` and health check patterns added to Dead-Letter section; `ConsumerName` default corrected; bus instantiation examples updated to `await using`.

---

## [2.2.0] — 2026-06-10

### New features

- **SQL Server message bus** (`TownSuite.WorkQueues.SqlServer`) — `SqlServerMessageBus` using `UPDLOCK + ROWLOCK + READPAST` for concurrent-safe polling; `SqlServerMigrationHostedService` for idempotent startup DDL; `SqlServerServiceExtensions` DI helpers. Requires SQL Server 2016+.

---

## [2.1.0] — 2026-06-09

### New features

- **Redis backend** (`TownSuite.WorkQueues.Redis`) — `RedisWorkQueue` (Redis Lists, FIFO) and `RedisMessageBus` (Redis Streams with consumer groups, automatic retry, dead-lettering, and `XAUTOCLAIM`-based reclaim). No database required.

---

## [2.0.0] — 2026-06-09

### Breaking changes

- **`Newtonsoft.Json` removed** from `TownSuite.WorkQueues`. If your application depended on the transitive reference, add an explicit package reference.

### Serialisation change (not breaking for most users)

`DbBackedWorkQueue` now writes payloads with `System.Text.Json` (compact JSON, no `$type`
metadata). **Reading** old Newtonsoft.Json payloads from the queue is handled automatically:

| Legacy payload type | Handled? |
|---|---|
| Simple POCO with `$type` annotation | ✓ — `$type` is silently ignored |
| Nested POCO, each level with `$type` | ✓ — ignored at every level |
| Collection root wrapped in `$values` (`List<T>`, `T[]`) | ✓ — `$values` array is extracted before deserialising |
| Polymorphic base-class usage (derived type written, base type read) | ✗ — derived-only properties are dropped; drain those channels before upgrading |

Draining the queue before upgrading is no longer required for the common case. The only
scenario that still requires a drain (or a manual replay) is polymorphic payloads where
the stored `$type` pointed to a concrete derived class and the call site deserialises to an
abstract base type — an unusual pattern.

- **Schema changes** (see migration notes below).

### Bug fixes

- **Crash in `PostgresMessageBus` polling loop** — `reader.GetDateTime(4)` was called on a column that is always `NULL` for unprocessed rows, throwing `InvalidCastException` on every poll cycle.

- **SQL parameter type mismatch in `PostgresMessageBus`** — The `channels` filter was passed as a comma-joined string but cast to `text[]`, causing a PostgreSQL type error. Now passed as a proper `string[]` via Npgsql.

- **Handler exceptions silently crashed the polling loop** — An unhandled exception thrown by any consumer would propagate through `Task.WhenAll`, roll back the entire batch, and restart the poll. All batched messages would retry indefinitely. Each message is now dispatched in its own `try/catch`; a failing message increments its retry count while successful messages in the same batch are still committed.

- **`PostgresMigrationHostedService` was entirely broken** — Four embedded-resource names contained the typo `WorkeQueue` instead of `WorkQueue`, causing every startup migration to throw `InvalidOperationException: Resource not found`. Fixed.

- **`DbBackedWorkQueue_NonDestructive.Dequeue` returned wrong result on SQL Server** — Reader was opened but never read; relied on output parameter value that isn't populated until after the reader closes. The method now reads from the result set first and falls back to the output parameter after reader disposal, covering both PostgreSQL (result-set OUT params) and SQL Server (post-close OUTPUT params).

### Security fixes

- **`TypeNameHandling.All` removed** — Newtonsoft.Json `TypeNameHandling.All` allows arbitrary type instantiation from attacker-controlled JSON. `DbBackedWorkQueue` now uses `System.Text.Json` with no type metadata.

### New features

- **Dead-letter queue** — Failed messages are retried up to `BatchOptions.MaxRetries` times (default 3). Once the limit is reached, `failedat` is set and the message is permanently excluded from polling. Dead-lettered rows remain in the table for inspection and can be replayed by clearing `failedat` and `retrycount`.

- **`BatchOptions.MaxRetries`** — new property (default `3`) that controls how many delivery attempts are made before dead-lettering.

- **`DbBackedWorkQueue` now implements `IWorkQueue`** — the base class carries the interface directly; `DbBackedWorkQueue_NonDestructive` retains it for documentation clarity.

- **Channel name validation** — `Enqueue` and `Publish` now throw if the channel name exceeds 500 characters.

### Performance

- **Partial index on `workqueue`** — Added `ix_workqueue_channel_unprocessed` on `(channel, timecreatedutc) WHERE timeprocessedutc IS NULL AND failedat IS NULL`, covering the hot polling query for both PostgreSQL and SQL Server.

### Schema migration notes

The `PostgresMigrationHostedService` applies all changes automatically on startup. For SQL Server, run the updated scripts in `scripts/sql-server/` manually.

| Change | SQL |
|---|---|
| Widen `channel` column | `ALTER TABLE workqueue ALTER COLUMN channel TYPE VARCHAR(500)` |
| Add `failedat` column | `ALTER TABLE workqueue ADD COLUMN IF NOT EXISTS failedat TIMESTAMP NULL` |
| Add `retrycount` column | `ALTER TABLE workqueue ADD COLUMN IF NOT EXISTS retrycount INT NOT NULL DEFAULT 0` |
| Add partial index | `CREATE INDEX IF NOT EXISTS ix_workqueue_channel_unprocessed ...` |

All PostgreSQL migration statements are idempotent (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`, `DO` block guards).

---

## [1.0.3] and earlier

Initial releases. Work queue and message bus backed by PostgreSQL and SQL Server.
Message bus and consumer lifecycle were marked as work in progress.
