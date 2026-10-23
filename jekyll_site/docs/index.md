---
layout: docs
title: Getting Started — TownSuite.WorkQueues
description: Install TownSuite.WorkQueues and send your first message in under five minutes.
permalink: /docs/
---

# Getting Started

TownSuite.WorkQueues is a .NET message bus and work queue backed by a table in the database you already run. No broker process, no new infrastructure — pick a backend and go.

## Install

Each backend is a separate NuGet package:

```sh
# PostgreSQL
dotnet add package TownSuite.WorkQueues.Postgres

# SQL Server
dotnet add package TownSuite.WorkQueues.SqlServer

# Redis
dotnet add package TownSuite.WorkQueues.Redis

# SQLite — local development only
dotnet add package TownSuite.WorkQueues.Sqlite
```

## Create the schema

**PostgreSQL and SQL Server** — register the migration hosted service to apply DDL automatically on startup, or run the scripts from the `scripts/` folder manually:

```csharp
// PostgreSQL
services.AddSingleton(new SqlTransportOptions
{
    ConnectionString = "Host=localhost;Database=myapp;Username=app;Password=secret",
    Schema = "transport"
});
services.AddPostgresMigrationHostedService();

// SQL Server
services.AddSingleton(new SqlServerTransportOptions
{
    ConnectionString = "Server=.;Database=myapp;Integrated Security=true",
    Schema = "dbo"
});
services.AddSqlServerMigrationHostedService();
```

**SQLite** — the migration service creates the file and table automatically. No SQL scripts needed:

```csharp
services.AddSingleton(new SqliteTransportOptions
{
    ConnectionString = "Data Source=./workqueue.db"
});
services.AddSqliteMigrationHostedService();
```

**Redis** — no schema needed. See the [Redis backend]({{ '/docs/backends/redis/' | relative_url }}) page for options.

## Define a message and consumer

```csharp
public record OrderSubmitted(Guid OrderId, string Customer);

public class OrderConsumer : IConsumer<OrderSubmitted>
{
    public async Task Consume(ConsumeContext<OrderSubmitted> ctx)
    {
        // ctx.Message    — typed payload
        // ctx.MessageId  — stable UUID, same across retries
        // ctx.SentTime   — when Publish() was called
        await ProcessOrder(ctx.Message);
        // returning normally acknowledges the message
        // throwing triggers a retry (up to MaxRetries times)
    }
}
```

## Register and publish

```csharp
// With DI (PostgreSQL example)
services.AddPostgresMessageBus((sp, bus) =>
{
    bus.Subscribe<OrderSubmitted, OrderConsumer>();
});

// Without DI
var bus = new PostgresMessageBus(options, logger);
bus.Subscribe(new OrderConsumer());

// Publish
await bus.Publish(new OrderSubmitted(Guid.NewGuid(), "alice@example.com"));

// Scheduled delivery — no earlier than 5 minutes from now
await bus.Publish(new ReminderEmail { UserId = userId },
    deliverAfter: DateTimeOffset.UtcNow.AddMinutes(5));
```

The polling loop starts when the bus is constructed. Messages are delivered within one `MaxWaitTime` cycle (default: 5 seconds).

## Next steps

- [Configuration reference]({{ '/docs/configuration/' | relative_url }}) — batch size, retries, and delays
- [Delivery controls]({{ '/docs/delivery-controls/' | relative_url }}) — graceful drain, tracing, priority, batch publish, size and rate limits
- [HTTPS server]({{ '/docs/http-server/' | relative_url }}) — publish and subscribe across data centres
- [PostgreSQL backend]({{ '/docs/backends/postgres/' | relative_url }})
- [SQL Server backend]({{ '/docs/backends/sqlserver/' | relative_url }})
- [Redis backend]({{ '/docs/backends/redis/' | relative_url }})
- [SQLite backend]({{ '/docs/backends/sqlite/' | relative_url }}) — multi-process local development
