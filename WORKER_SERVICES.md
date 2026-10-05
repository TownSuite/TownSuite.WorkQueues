# Using TownSuite.WorkQueues with Worker Services

This guide shows how to integrate the message bus and work queue into a .NET Worker Service
(background process) or an ASP.NET Core web application that also runs background consumers.
It is written for both humans and AI coding assistants.

---

## Contents

- [Concepts and lifecycle](#concepts-and-lifecycle)
- [PostgreSQL — Worker Service](#postgresql--worker-service)
- [Redis — Worker Service](#redis--worker-service)
- [ASP.NET Core — web + background in one process](#aspnet-core--web--background-in-one-process)
- [Consumers that need scoped services](#consumers-that-need-scoped-services)
- [Multiple message types](#multiple-message-types)
- [Publishing from anywhere in the application](#publishing-from-anywhere-in-the-application)
- [Production settings: concurrency, retries, faults and expiry](#production-settings-concurrency-retries-faults-and-expiry)
- [Health checks, metrics and purging](#health-checks-metrics-and-purging)
- [Configuration via appsettings.json](#configuration-via-appsettingsjson)
- [Deploying as a Windows Service or systemd unit](#deploying-as-a-windows-service-or-systemd-unit)
- [Checklist for AI coding assistants](#checklist-for-ai-coding-assistants)

---

## Concepts and lifecycle

### How the bus fits into the hosted service model

Both `PostgresMessageBus` and `RedisMessageBus` start a background polling loop the moment
they are constructed. To integrate cleanly with ASP.NET Core's hosted service lifecycle:

1. **Delay construction** — resolve the bus singleton inside a `IHostedService.StartAsync`,
   not in a constructor or at registration time. This guarantees the bus starts *after* the
   migrations hosted service has finished running.

2. **Stop on shutdown** — every bus implements `IAsyncDisposable`. Calling `DisposeAsync` cancels
   the polling loops and waits up to 10 seconds for in-flight dispatches to complete. (The buses do
   not implement `IDisposable`, so `(bus as IDisposable)?.Dispose()` silently does nothing.)

The pattern below wraps both concerns in a single `MessageBusHostedService` that every
example in this guide reuses.

```csharp
// Infrastructure/MessageBusHostedService.cs
internal sealed class MessageBusHostedService : IHostedService
{
    private readonly IServiceProvider _sp;
    private IMessageBus? _bus;

    public MessageBusHostedService(IServiceProvider sp) => _sp = sp;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolve here (not in the constructor) so that any migration hosted
        // service registered before this one has already completed.
        _bus = _sp.GetRequiredService<IMessageBus>();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bus != null) await _bus.DisposeAsync();
    }
}
```

### Registration order matters

Register services in this order so migrations run before the bus starts processing:

```
1. AddPostgresMigrationHostedService()   — runs DDL on startup
2. AddSingleton<IMessageBus>(factory)    — deferred; created only when first resolved
3. AddHostedService<MessageBusHostedService>() — resolves the bus in StartAsync
```

For Redis there is no migration step, so only steps 2 and 3 are needed.

---

## PostgreSQL — Worker Service

### Create the project

```bash
dotnet new worker -n OrderProcessor
cd OrderProcessor
dotnet add package TownSuite.WorkQueues
dotnet add package TownSuite.WorkQueues.Postgres
```

### Project structure

```
OrderProcessor/
├── appsettings.json
├── Program.cs
├── Infrastructure/
│   └── MessageBusHostedService.cs
├── Messages/
│   └── OrderSubmitted.cs
└── Consumers/
    └── OrderConsumer.cs
```

### Messages/OrderSubmitted.cs

```csharp
namespace OrderProcessor.Messages;

public class OrderSubmitted
{
    public Guid OrderId { get; set; }
    public string CustomerEmail { get; set; } = string.Empty;
    public decimal Total { get; set; }
}
```

### Consumers/OrderConsumer.cs

```csharp
using Microsoft.Extensions.Logging;
using OrderProcessor.Messages;
using TownSuite.WorkQueues;

namespace OrderProcessor.Consumers;

public class OrderConsumer : IConsumer<OrderSubmitted>
{
    private readonly ILogger<OrderConsumer> _logger;

    public OrderConsumer(ILogger<OrderConsumer> logger) => _logger = logger;

    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        var order = context.Message;
        _logger.LogInformation("Processing order {OrderId} for {Email} — total {Total:C}",
            order.OrderId, order.CustomerEmail, order.Total);

        // Do real work here: call APIs, write to a database, send emails, etc.
        await Task.CompletedTask;

        // Throw any exception to trigger a retry (up to MaxRetries).
        // After MaxRetries the message is dead-lettered and excluded from future polls.
    }
}
```

### Infrastructure/MessageBusHostedService.cs

```csharp
using TownSuite.WorkQueues;

namespace OrderProcessor.Infrastructure;

internal sealed class MessageBusHostedService : IHostedService
{
    private readonly IServiceProvider _sp;
    private IMessageBus? _bus;

    public MessageBusHostedService(IServiceProvider sp) => _sp = sp;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _bus = _sp.GetRequiredService<IMessageBus>();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bus != null) await _bus.DisposeAsync();
    }
}
```

### Program.cs

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderProcessor.Consumers;
using OrderProcessor.Infrastructure;
using OrderProcessor.Messages;
using TownSuite.WorkQueues;
using TownSuite.WorkQueues.Postgres;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        // 1. Options
        services.AddSingleton(new SqlTransportOptions
        {
            ConnectionString      = cfg.GetConnectionString("WorkQueue")!,
            AdminConnectionString = cfg.GetConnectionString("WorkQueueAdmin")!,
            Schema                = "transport",
            MaxBatchSize          = 50,
            MaxWaitTime           = TimeSpan.FromSeconds(2),
            MaxRetries            = 3
        });

        // 2. Run DDL migrations on startup (creates the table, stored procs, index).
        //    Hosted services start in registration order, so this runs before the bus.
        services.AddPostgresMigrationHostedService();

        // 3. Bus singleton — not created yet; the factory runs lazily on first resolve.
        services.AddSingleton<IMessageBus>(sp =>
        {
            var options = sp.GetRequiredService<SqlTransportOptions>();
            var logger  = sp.GetRequiredService<ILogger<PostgresMessageBus>>();
            var bus     = new PostgresMessageBus(options, logger);

            // Register every consumer before the bus starts delivering messages.
            bus.Subscribe(sp.GetRequiredService<OrderConsumer>());

            return bus;
        });

        // 4. Register consumers so IServiceProvider can inject their dependencies.
        services.AddTransient<OrderConsumer>();

        // 5. Hosted service that resolves the bus in StartAsync (after migrations).
        services.AddHostedService<MessageBusHostedService>();
    })
    .Build();

await host.RunAsync();
```

### appsettings.json

```json
{
  "ConnectionStrings": {
    "WorkQueue":      "Host=localhost;Port=5432;Database=myapp;Username=app;Password=secret",
    "WorkQueueAdmin": "Host=localhost;Port=5432;Database=myapp;Username=admin;Password=secret"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "TownSuite": "Debug"
    }
  }
}
```

---

## Redis — Worker Service

### Create the project

```bash
dotnet new worker -n OrderProcessor
cd OrderProcessor
dotnet add package TownSuite.WorkQueues
dotnet add package TownSuite.WorkQueues.Redis
```

### Program.cs

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderProcessor.Consumers;
using OrderProcessor.Infrastructure;
using StackExchange.Redis;
using TownSuite.WorkQueues;
using TownSuite.WorkQueues.Redis;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        // 1. Shared connection multiplexer (one per process — StackExchange.Redis best practice).
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(cfg.GetConnectionString("Redis")!));

        // 2. Bus options.
        services.AddSingleton(new RedisOptions
        {
            KeyPrefix     = "myapp",
            ConsumerGroup = "order-workers",
            MaxBatchSize  = 50,
            MaxWaitTime   = TimeSpan.FromSeconds(2),
            MaxRetries    = 3
        });

        // 3. Bus singleton.
        services.AddSingleton<IMessageBus>(sp =>
        {
            var redis   = sp.GetRequiredService<IConnectionMultiplexer>();
            var options = sp.GetRequiredService<RedisOptions>();
            var logger  = sp.GetRequiredService<ILogger<RedisMessageBus>>();
            var bus     = new RedisMessageBus(redis, options, logger);

            bus.Subscribe(sp.GetRequiredService<OrderConsumer>());

            return bus;
        });

        // 4. Register consumers.
        services.AddTransient<OrderConsumer>();

        // 5. Lifecycle management.
        services.AddHostedService<MessageBusHostedService>();
    })
    .Build();

await host.RunAsync();
```

### appsettings.json

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  }
}
```

Everything else (consumer classes, `MessageBusHostedService`) is identical to the PostgreSQL
example.

---

## ASP.NET Core — web + background in one process

A common pattern is to run an HTTP API and background consumers in the same process. The bus
is registered the same way; ASP.NET Core's hosted service infrastructure manages its lifetime.

```csharp
// Program.cs (minimal API style)
using Microsoft.Extensions.Logging;
using OrderProcessor.Consumers;
using OrderProcessor.Infrastructure;
using TownSuite.WorkQueues;
using TownSuite.WorkQueues.Postgres;

var builder = WebApplication.CreateBuilder(args);

// --- background processing setup ---

builder.Services.AddSingleton(new SqlTransportOptions
{
    ConnectionString      = builder.Configuration.GetConnectionString("WorkQueue")!,
    AdminConnectionString = builder.Configuration.GetConnectionString("WorkQueueAdmin")!,
    Schema     = "transport",
    MaxRetries = 3
});

builder.Services.AddPostgresMigrationHostedService();

builder.Services.AddSingleton<IMessageBus>(sp =>
{
    var options = sp.GetRequiredService<SqlTransportOptions>();
    var logger  = sp.GetRequiredService<ILogger<PostgresMessageBus>>();
    var bus     = new PostgresMessageBus(options, logger);

    bus.Subscribe(sp.GetRequiredService<OrderConsumer>());
    return bus;
});

builder.Services.AddTransient<OrderConsumer>();
builder.Services.AddHostedService<MessageBusHostedService>();

// --- web API setup ---

builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();

// --- publish from an HTTP endpoint ---

app.MapPost("/orders", async (OrderSubmitted order, IMessageBus bus) =>
{
    await bus.Publish(order);
    return Results.Accepted();
});

app.Run();
```

The `IMessageBus` singleton is available for injection into controllers and minimal API
handlers. `Publish` is safe to call from any thread at any time after the application starts.

---

## Consumers that need scoped services

Consumers are registered as transient/singleton and are held by the bus. If a consumer needs a
**scoped** service — such as `DbContext`, `HttpClient` (via `IHttpClientFactory`), or a
repository — inject `IServiceScopeFactory` and create a scope per message.

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessor.Data;
using OrderProcessor.Messages;
using TownSuite.WorkQueues;

public class OrderConsumer : IConsumer<OrderSubmitted>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public OrderConsumer(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        // Each message gets its own DI scope — DbContext, transactions, etc.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Orders.Add(new Order
        {
            Id    = context.Message.OrderId,
            Email = context.Message.CustomerEmail,
            Total = context.Message.Total
        });

        await db.SaveChangesAsync();
    }
}
```

Register it in `Program.cs` as a transient (or singleton — the scope inside `Consume` is what
matters, not the consumer's own lifetime):

```csharp
services.AddTransient<OrderConsumer>();
services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("App")));
```

---

## Multiple message types

Register each consumer type in the bus factory and register each consumer class in DI.

```csharp
services.AddTransient<OrderConsumer>();
services.AddTransient<InvoiceConsumer>();
services.AddTransient<ShipmentConsumer>();

services.AddSingleton<IMessageBus>(sp =>
{
    var options = sp.GetRequiredService<SqlTransportOptions>();
    var logger  = sp.GetRequiredService<ILogger<PostgresMessageBus>>();
    var bus     = new PostgresMessageBus(options, logger);

    bus.Subscribe(sp.GetRequiredService<OrderConsumer>());
    bus.Subscribe(sp.GetRequiredService<InvoiceConsumer>());
    bus.Subscribe(sp.GetRequiredService<ShipmentConsumer>());

    return bus;
});
```

Multiple consumers subscribed to the **same type** each receive an independent copy of every
message. Subscriptions to **different types** share the same polling loop; the bus dispatches
all subscribed types in each cycle.

---

## Publishing from anywhere in the application

Inject `IMessageBus` directly into any class to publish. There are no connection arguments —
the bus manages its own connection internally.

```csharp
public class OrderService
{
    private readonly IMessageBus _bus;
    private readonly AppDbContext _db;

    public OrderService(IMessageBus bus, AppDbContext db)
    {
        _bus = bus;
        _db  = db;
    }

    public async Task PlaceOrderAsync(PlaceOrderRequest req)
    {
        var order = new Order { Id = Guid.NewGuid(), Email = req.Email };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Publish after the database write commits.
        // This is not transactional with the SaveChanges above — see below.
        await _bus.Publish(new OrderSubmitted
        {
            OrderId       = order.Id,
            CustomerEmail = order.Email,
            Total         = req.Total
        });
    }
}
```

### Publishing inside your own transaction

To publish only if your database write commits, pass the same open connection and transaction to
`Publish`. The message row commits or rolls back with your own rows:

```csharp
public async Task PlaceOrderAsync(PlaceOrderRequest req, CancellationToken ct)
{
    await using var cn = new NpgsqlConnection(_connectionString);
    await cn.OpenAsync(ct);
    await using var tx = await cn.BeginTransactionAsync(ct);

    var orderId = Guid.NewGuid();
    await cn.ExecuteAsync("INSERT INTO orders (id, email) VALUES (@orderId, @email)",
        new { orderId, email = req.Email }, tx);

    await _bus.Publish(new OrderSubmitted { OrderId = orderId, CustomerEmail = req.Email }, cn, tx);

    await tx.CommitAsync(ct);
}
```

With Entity Framework Core, use `db.Database.GetDbConnection()` and
`db.Database.CurrentTransaction!.GetDbTransaction()` inside `BeginTransactionAsync`. On Redis, pass an
`ITransaction` instead: `redisBus.Publish(message, transaction)`. See
[README — Transactional Publish](Readme.md#transactional-publish-outbox).

---

## Production settings: concurrency, retries, faults and expiry

The examples above use defaults. A production worker usually sets these as well:

```csharp
services.AddSingleton(new SqlServerTransportOptions
{
    ConnectionString = cfg.GetConnectionString("WorkQueue")!,

    // Throughput: several polling loops per bus. With a lease, no transaction or row lock is
    // held while consumers run, so slow consumers (external APIs) don't block anything.
    MaxConcurrency = 4,
    MaxBatchSize   = 10,
    ClaimLease     = TimeSpan.FromMinutes(2),       // longer than the slowest consumer

    // Retries: 5 attempts, 1s, 2s, 4s, 8s apart (capped at 1 min), transient errors only.
    MaxRetries             = 5,
    RetryDelay             = TimeSpan.FromSeconds(1),
    RetryBackoffMultiplier = 2.0,
    MaxRetryDelay          = TimeSpan.FromMinutes(1),
    IsRetryable            = ex => ex is not ValidationException,

    // Faults are delivered at least once; one that throws is redelivered after this delay.
    FaultRedeliveryDelay = TimeSpan.FromMinutes(1)
});

services.AddSqlServerMigrationHostedService();

services.AddSingleton<IMessageBus>(sp =>
{
    var bus = new SqlServerMessageBus(
        sp.GetRequiredService<SqlServerTransportOptions>(),
        sp.GetRequiredService<ILogger<SqlServerMessageBus>>(),
        sp);

    bus.Subscribe<OrderSubmitted, OrderConsumer>();              // scoped consumer, resolved per message
    bus.SubscribeFault(sp.GetRequiredService<OrderFaultHandler>()); // runs when a message is dead-lettered
    return bus;
});

services.AddScoped<OrderConsumer>();
services.AddSingleton<OrderFaultHandler>();
services.AddHostedService<MessageBusHostedService>();
```

A fault consumer records the failure where people will see it. It can run more than once for the
same message (`IsRedelivery`), so make it idempotent:

```csharp
internal sealed class OrderFaultHandler(ILogger<OrderFaultHandler> logger) : IConsumer<Fault<OrderSubmitted>>
{
    public Task Consume(ConsumeContext<Fault<OrderSubmitted>> ctx)
    {
        var fault = ctx.Message;
        logger.LogWarning("Order {OrderId} dead-lettered after {Attempts} attempts ({Reason}): {Error}",
            fault.OriginalMessage.OrderId, fault.AttemptCount,
            fault.Expired ? "expired" : fault.NonRetryable ? "non-retryable" : "retries exhausted",
            fault.ExceptionMessage);
        // e.g. mark the order as failed, alert support. Replay later with
        // bus.ReplayDeadLettered<OrderSubmitted>(fault.MessageId).
        return Task.CompletedTask;
    }
}
```

To stop work that is pointless if it runs late, give the message an expiry when you publish it. It
is dead-lettered without calling consumers, and the fault has `Expired = true`:

```csharp
await bus.Publish(new CartHoldRequested { CartId = cartId },
    new PublishOptions { TimeToLive = TimeSpan.FromMinutes(10) });
```

> Keep `MaxConcurrency = 1` (and no lease) when a channel needs strict ordering. Without
> `ClaimLease`, keep `MaxBatchSize = 1` for consumers that call external services: the claimed batch
> holds its transaction until every message in it is handled.

---

## Health checks, metrics and purging

```csharp
// Health: Unhealthy if polling stopped; Degraded when a queue threshold is exceeded.
// Package: TownSuite.WorkQueues.HealthChecks
builder.Services.AddHealthChecks().AddMessageBus(configure: o => o
    .Queue<OrderSubmitted>(q =>
    {
        q.MaxOldestReadyAge = TimeSpan.FromMinutes(1);   // stopped worker / no subscriber
        q.MaxPendingFaults  = 0;                         // fault consumer keeps failing
    }));
app.MapHealthChecks("/healthz");

// Metrics: counters and the consumer-duration histogram are always emitted; TrackQueue adds
// backlog gauges, refreshed in the background.
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(WorkQueueMetrics.MeterName).AddPrometheusExporter());
```

Queue gauges and purging both fit in one small hosted service:

```csharp
internal sealed class QueueMaintenanceService(IMessageBus bus, ILogger<QueueMaintenanceService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var tracking = WorkQueueMetrics.TrackQueue<OrderSubmitted>(bus, TimeSpan.FromSeconds(30));

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                long processed = await bus.PurgeProcessed(DateTimeOffset.UtcNow.AddDays(-30), stoppingToken);
                long dead      = await bus.PurgeDeadLettered<OrderSubmitted>(DateTimeOffset.UtcNow.AddDays(-90), stoppingToken);
                logger.LogInformation("Purged {Processed} processed and {Dead} dead-lettered messages", processed, dead);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Queue purge failed; it will run again on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

builder.Services.AddHostedService<QueueMaintenanceService>();
```

Run the purge from one instance only (or accept that several instances share the work; the deletes
are batched and safe to run concurrently).

---

## Configuration via appsettings.json

Bind options from configuration instead of hard-coding values.

### Strongly-typed options class

```csharp
// Infrastructure/WorkQueueSettings.cs
public class WorkQueueSettings
{
    public string ConnectionString      { get; set; } = string.Empty;
    public string AdminConnectionString { get; set; } = string.Empty;
    public string Schema                { get; set; } = "transport";
    public int    MaxBatchSize          { get; set; } = 50;
    public int    MaxRetries            { get; set; } = 3;
    public double MaxWaitSeconds        { get; set; } = 2;
    public int    MaxConcurrency        { get; set; } = 1;
    public double? ClaimLeaseSeconds    { get; set; }
    public double RetryDelaySeconds     { get; set; }
    public double RetryBackoffMultiplier { get; set; } = 1.0;
    public double? MaxRetryDelaySeconds { get; set; }
    public double FaultRedeliveryDelaySeconds { get; set; } = 60;
}
```

### Program.cs binding

```csharp
var settings = builder.Configuration
    .GetSection("WorkQueue")
    .Get<WorkQueueSettings>()
    ?? throw new InvalidOperationException("WorkQueue configuration section is missing.");

builder.Services.AddSingleton(new SqlTransportOptions
{
    ConnectionString      = settings.ConnectionString,
    AdminConnectionString = settings.AdminConnectionString,
    Schema                = settings.Schema,
    MaxBatchSize          = settings.MaxBatchSize,
    MaxWaitTime           = TimeSpan.FromSeconds(settings.MaxWaitSeconds),
    MaxRetries            = settings.MaxRetries,
    MaxConcurrency        = settings.MaxConcurrency,
    ClaimLease            = settings.ClaimLeaseSeconds is { } lease ? TimeSpan.FromSeconds(lease) : null,
    RetryDelay            = TimeSpan.FromSeconds(settings.RetryDelaySeconds),
    RetryBackoffMultiplier = settings.RetryBackoffMultiplier,
    MaxRetryDelay         = settings.MaxRetryDelaySeconds is { } max ? TimeSpan.FromSeconds(max) : null,
    FaultRedeliveryDelay  = TimeSpan.FromSeconds(settings.FaultRedeliveryDelaySeconds)
});
```

### appsettings.json

```json
{
  "WorkQueue": {
    "ConnectionString":      "Host=db;Database=myapp;Username=app;Password=secret",
    "AdminConnectionString": "Host=db;Database=myapp;Username=admin;Password=secret",
    "Schema":                "transport",
    "MaxBatchSize":          50,
    "MaxRetries":            5,
    "MaxWaitSeconds":        2,
    "MaxConcurrency":        4,
    "ClaimLeaseSeconds":     120,
    "RetryDelaySeconds":     1,
    "RetryBackoffMultiplier": 2.0,
    "MaxRetryDelaySeconds":  60,
    "FaultRedeliveryDelaySeconds": 60
  }
}
```

Override per environment in `appsettings.Production.json`, environment variables
(`WorkQueue__ConnectionString=...`), or Docker/Kubernetes secrets.

---

## Deploying as a Windows Service or systemd unit

### Windows Service

Add the hosting extension and publish as a single-file self-contained executable.

```bash
dotnet add package Microsoft.Extensions.Hosting.WindowsServices
```

```csharp
// Program.cs — add one line
var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()   // <-- add this
    .ConfigureServices(...)
    .Build();
```

```bash
dotnet publish -c Release -r win-x64 --self-contained -o ./publish

sc create OrderProcessor binPath= "C:\services\OrderProcessor\OrderProcessor.exe"
sc start OrderProcessor
```

### systemd (Linux)

```bash
dotnet add package Microsoft.Extensions.Hosting.Systemd
```

```csharp
var host = Host.CreateDefaultBuilder(args)
    .UseSystemd()          // <-- add this
    .ConfigureServices(...)
    .Build();
```

```bash
dotnet publish -c Release -r linux-x64 --self-contained -o /opt/order-processor
```

```ini
# /etc/systemd/system/order-processor.service
[Unit]
Description=OrderProcessor worker service
After=network.target

[Service]
WorkingDirectory=/opt/order-processor
ExecStart=/opt/order-processor/OrderProcessor
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
Environment=WorkQueue__ConnectionString=Host=db;Database=myapp;Username=app;Password=secret

[Install]
WantedBy=multi-user.target
```

```bash
systemctl enable order-processor
systemctl start order-processor
journalctl -u order-processor -f   # tail logs
```

Both `UseWindowsService()` and `UseSystemd()` wire up graceful shutdown signals so
`IHostedService.StopAsync` is called correctly, giving the bus time to finish in-flight
dispatches before the process exits.

---

## Checklist for AI coding assistants

When asked to add message bus processing to a Worker Service or ASP.NET Core app:

1. **Check whether Postgres or Redis is in use.** Different packages, different option types
   (`SqlTransportOptions` vs `RedisOptions`), different connection setup.

2. **Identify all message types** the app needs to consume. Each needs an
   `IConsumer<T>` class and a `bus.Subscribe(...)` call.

3. **Check whether consumers need scoped services** (DbContext, repositories, HttpClient).
   If yes, inject `IServiceScopeFactory` and create a scope inside `Consume`, not in the
   constructor.

4. **Registration order for PostgreSQL:**
   - `AddPostgresMigrationHostedService()` first
   - `AddSingleton<IMessageBus>(factory)` second
   - `AddHostedService<MessageBusHostedService>()` third
   This ensures migrations complete before the bus starts polling.

5. **For Redis:** no migration step. Register `IConnectionMultiplexer` as a singleton from
   a connection string (one multiplexer per process). Then register `RedisOptions` and the bus.

6. **Wire `MessageBusHostedService`** — resolve `IMessageBus` inside `StartAsync`, not in
   the constructor. `await bus.DisposeAsync()` in `StopAsync` (the buses are `IAsyncDisposable` only).

7. **Register all consumer classes in DI** (`AddTransient<TConsumer>`) so their own
   dependencies (loggers, `IServiceScopeFactory`, etc.) are injected.

8. **Do NOT call `Subscribe` after the application has started processing messages** — add all
   subscriptions inside the factory delegate before returning the bus.

9. **For Windows Service deployment:** add `UseWindowsService()` to the host builder.
   For Linux systemd: add `UseSystemd()`. Both ensure `StopAsync` fires on shutdown signals.

10. **Configuration:** prefer `appsettings.json` + environment variable overrides over
    hard-coded connection strings. Use `IConfiguration.GetSection(...).Get<T>()` to bind.

11. **Choose a claim mode.** Consumers that call external services: set `ClaimLease` (longer than
    the slowest consumer), or keep `MaxBatchSize = 1`. Use `MaxConcurrency` for throughput, and keep
    it at `1` where order matters.

12. **Retry only what can succeed.** Set `IsRetryable` so validation/business failures dead-letter at
    once, and `RetryDelay` + `RetryBackoffMultiplier` for transient ones. Better still, handle expected
    business outcomes inside the consumer and return normally.

13. **Subscribe a `Fault<T>` consumer** for every message type whose failure someone must act on, and
    make it idempotent — faults are delivered at least once.

14. **Publish inside the caller's transaction** with `Publish(message, connection, transaction)` when
    the message must only exist if the business write commits. Do not use the obsolete `IWorkQueue`.

15. **Add the health check and a purge job** (`AddMessageBus`, `PurgeProcessed`) so stopped workers,
    growing backlogs and unbounded tables are noticed.
