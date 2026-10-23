---
layout: docs
title: Redis Backend — TownSuite.WorkQueues
description: Set up the Redis Streams-backed message bus for in-memory work queues.
permalink: /docs/backends/redis/
---

# Redis Backend

The Redis backend uses [Redis Streams](https://redis.io/docs/data-types/streams/) with consumer groups, which provide at-least-once delivery and automatic pending-entry reclaim when a consumer goes away.

<div class="callout">
<strong>Durability note.</strong> Redis is in-memory by default. Without RDB or AOF persistence enabled, messages are lost if Redis restarts. For durable work queues, use the PostgreSQL or SQL Server backend.
</div>

## Install

```sh
dotnet add package TownSuite.WorkQueues.Redis
```

No SQL scripts or migration service required.

## Register the bus

```csharp
builder.Services.AddSingleton(new RedisOptions
{
    ConnectionString = "localhost:6379",
    KeyPrefix = "workqueue",
    ConsumerGroup = "default"
});

builder.Services.AddRedisMessageBus((sp, bus) =>
{
    bus.Subscribe(new OrderConsumer());
    // or DI-scoped:
    bus.Subscribe<OrderSubmitted, OrderConsumer>();
});
```

## Options reference

See [`RedisOptions`]({{ '/docs/configuration/' | relative_url }}#redis--redisoptions) for the full property table. Common values:

```csharp
new RedisOptions
{
    ConnectionString = "localhost:6379,password=secret",
    KeyPrefix = "myapp:workqueue",   // namespace all keys under your app prefix
    ConsumerGroup = "workers",
    ConsumerName = "worker-1",       // override default {MachineName}-{ProcessId}
    ReclaimIdleTime = TimeSpan.FromMinutes(2),
    MaxBatchSize = 100,
    MaxWaitTime = TimeSpan.FromSeconds(5),
    MaxRetries = 3
}
```

## How claiming works

The bus uses `XREADGROUP` to read pending entries from a Redis Stream. Each consumer has a distinct `ConsumerName` so that multiple processes on the same host maintain separate pending-entry lists and do not reclaim each other's in-flight messages.

When a consumer goes offline without acknowledging its messages, those entries become idle. The bus periodically calls `XAUTOCLAIM` to transfer entries idle longer than `ReclaimIdleTime` to the current consumer for redelivery.

Messages are acknowledged with `XACK` after successful processing. A failed message increments its retry count and is requeued; after `MaxRetries` attempts it is dead-lettered (moved to a `{KeyPrefix}:deadletter` stream).

A stream has no ordering other than arrival, so `PublishOptions.Priority` is not supported: a non-zero priority throws `NotSupportedException`. When a drain releases a claimed entry, the entry is re-added with its attempt count and the old one acknowledged, so it can be claimed again at once, at the end of the stream. The trace context is stored in a `tracecontext` stream field.
