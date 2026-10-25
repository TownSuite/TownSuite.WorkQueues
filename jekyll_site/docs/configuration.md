---
layout: docs
title: Configuration — TownSuite.WorkQueues
description: Reference for BatchOptions and all per-backend configuration properties.
permalink: /docs/configuration/
---

# Configuration

All backend options classes inherit from `BatchOptions`, which controls polling and retry behavior. Each backend then adds its own connection and schema properties.

## BatchOptions (shared)

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxBatchSize` | `int` | `100` | Maximum messages to claim and process per poll cycle. |
| `MaxWaitTime` | `TimeSpan` | `5s` | How long to wait between poll cycles when no messages are found. |
| `ContinuousPolling` | `bool` | `false` | When `true`, the bus polls again immediately after each batch instead of waiting `MaxWaitTime`. Useful for high-throughput scenarios. |
| `MaxRetries` | `int` | `3` | Maximum delivery attempts before a message is dead-lettered. |
| `RetryDelay` | `TimeSpan` | `0` | Time to hold a failed message before making it eligible for the next retry attempt. |
| `DrainTimeout` | `TimeSpan` | `30s` | How long `DisposeAsync` lets in-flight messages finish before cancelling their consumers. See `IMessageBus.DrainAsync`. |
| `MaxMessageSize` | `int?` | `null` | Largest serialised message in bytes. Larger messages throw `MessageTooLargeException` at publish, and are dead-lettered unread if found in the store. |
| `MessagesPerSecond` | `double` | `0` | Maximum deliveries per second for this bus, across its polling loops. `0` is no limit. |

Per-message options are set with `PublishOptions` on `Publish` and `PublishBatch`: `DeliverAfter`, `ExpiresAt` / `TimeToLive`, and `Priority` (lower is delivered first; not supported on Redis).

## PostgreSQL — `SqlTransportOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `ConnectionString` | `string` | — | Npgsql connection string used for message transport. |
| `Schema` | `string` | `"transport"` | PostgreSQL schema that contains the `workqueue` table. |
| `AdminConnectionString` | `string` | _(same as ConnectionString)_ | Connection string for DDL migrations. May require broader privileges than the runtime connection. |

## SQL Server — `SqlServerTransportOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `ConnectionString` | `string` | — | Microsoft.Data.SqlClient connection string. |
| `Schema` | `string` | `"dbo"` | SQL Server schema that contains the `workqueue` table. |
| `AdminConnectionString` | `string` | _(same as ConnectionString)_ | Connection string for DDL migrations. |

## Redis — `RedisOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `ConnectionString` | `string` | — | StackExchange.Redis connection string. |
| `KeyPrefix` | `string` | `"workqueue"` | Prefix applied to all Redis keys created by this library. |
| `ConsumerGroup` | `string` | `"default"` | Redis Streams consumer group name. |
| `ConsumerName` | `string` | `{MachineName}-{ProcessId}` | Consumer instance name within the group. Override to a stable value in tests. |
| `ReclaimIdleTime` | `TimeSpan?` | `3 × MaxWaitTime` | How long a pending message must be idle before another consumer may reclaim it. |

## SQLite — `SqliteTransportOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `ConnectionString` | `string` | — | SQLite connection string. Example: `Data Source=./workqueue.db` |
| `LockTimeout` | `TimeSpan` | `60s` | How long a claimed message is held before another process may reclaim it if the claimant crashes. Set this to comfortably exceed the longest expected consumer processing time. |

## Retry and dead-letter behavior

When a consumer throws an exception, the message is retried up to `MaxRetries` times. If `RetryDelay` is set, the message is held for that duration before becoming eligible again. After the final attempt, the row is marked `failedat` and polling skips it permanently.

To recover dead-lettered messages:

```csharp
// Reset all dead-lettered messages of this type for redelivery
int replayed = await bus.ReplayDeadLettered<OrderSubmitted>();
```

To receive a notification when a message is dead-lettered, register a fault consumer:

```csharp
bus.SubscribeFault<OrderSubmitted>(new OrderAlertConsumer());

class OrderAlertConsumer : IConsumer<Fault<OrderSubmitted>>
{
    public Task Consume(ConsumeContext<Fault<OrderSubmitted>> ctx)
    {
        var f = ctx.Message;
        // f.OriginalMessage  — the original payload
        // f.ExceptionType    — full type name of the exception
        // f.ExceptionMessage — exception message
        // f.StackTrace       — full stack trace
        // f.AttemptCount     — how many times delivery was attempted
        // f.FaultedAt        — when it was dead-lettered
        return Notify(f);
    }
}
```

## ConsumeContext properties

Every consumer receives a `ConsumeContext<T>` with the following members:

| Member | Type | Description |
|---|---|---|
| `Message` | `T` | The deserialized payload. |
| `MessageId` | `Guid` | Stable UUID assigned at publish time. Identical across all retry attempts for the same message. |
| `SentTime` | `DateTimeOffset` | Timestamp from when `Publish()` was called. |
| `CancellationToken` | `CancellationToken` | Cancelled when a drain runs out of time (`DrainAsync`, or `DisposeAsync` after `DrainTimeout`). Pass it to your I/O so shutdown can stop the consumer. |

See [Delivery controls]({{ '/docs/delivery-controls/' | relative_url }}) for drain, tracing, priority, batch publish, size and rate limits, and [HTTPS server]({{ '/docs/http-server/' | relative_url }}) for the server's `WorkQueuesServer` settings.
