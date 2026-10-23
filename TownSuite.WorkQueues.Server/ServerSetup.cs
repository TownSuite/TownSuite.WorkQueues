using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using TownSuite.WorkQueues.Server.Stores;

namespace TownSuite.WorkQueues.Server;

internal static class ServerSetup
{
    public static IServiceCollection AddTopicServer(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TopicServerOptions.SectionName).Get<TopicServerOptions>() ?? new TopicServerOptions();
        services.AddSingleton(options);
        services.AddSingleton(Options.Create(options));

        switch (options.Backend.ToLowerInvariant())
        {
            case "sqlserver":
                services.AddSingleton<ITopicStore, SqlServerTopicStore>();
                break;
            case "postgres":
                services.AddSingleton<ITopicStore, PostgresTopicStore>();
                break;
            case "redis":
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options.ConnectionString
                    ?? throw new InvalidOperationException($"{TopicServerOptions.SectionName}:ConnectionString is required for the redis backend.")));
                services.AddSingleton<ITopicStore, RedisTopicStore>();
                break;
            case "memory":
                services.AddSingleton<ITopicStore, InMemoryTopicStore>();
                break;
            default:
                throw new InvalidOperationException($"{TopicServerOptions.SectionName}:Backend must be sqlserver, postgres, redis or memory, not '{options.Backend}'.");
        }

        services.AddHostedService<TopicStoreMaintenance>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt => ConfigureJwt(jwt, options.Jwt));
        services.AddAuthorization();
        services.AddProblemDetails();
        services.AddHealthChecks().AddCheck<TopicStoreHealthCheck>("topic-store");

        services.Configure<ForwardedHeadersOptions>(f =>
        {
            f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // The ingress is the only route in; its address is not known in advance.
            f.KnownIPNetworks.Clear();
            f.KnownProxies.Clear();
        });
        return services;
    }

    private static void ConfigureJwt(JwtBearerOptions jwt, JwtSettings settings)
    {
        // Keep claim types as issued ("scope", not a mapped URI).
        jwt.MapInboundClaims = false;
        jwt.RequireHttpsMetadata = settings.RequireHttpsMetadata;

        if (!string.IsNullOrEmpty(settings.Authority))
        {
            jwt.Authority = settings.Authority;
            jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(settings.Audience);
            jwt.TokenValidationParameters.ValidAudience = settings.Audience;
            if (!string.IsNullOrEmpty(settings.Issuer)) jwt.TokenValidationParameters.ValidIssuer = settings.Issuer;
            return;
        }

        if (string.IsNullOrEmpty(settings.SigningKey) || string.IsNullOrEmpty(settings.Issuer) || string.IsNullOrEmpty(settings.Audience))
            throw new InvalidOperationException(
                $"{TopicServerOptions.SectionName}:Jwt needs an Authority, or a SigningKey with an Issuer and Audience.");

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer              = settings.Issuer,
            ValidAudience            = settings.Audience,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.FromMinutes(1)
        };
    }

    public static WebApplication UseTopicServer(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<TopicServerOptions>();
        if (options.TrustForwardedHeaders) app.UseForwardedHeaders();

        // Probes reach /healthz over plain HTTP inside the cluster; everything else must be HTTPS.
        if (options.RequireHttps)
        {
            app.Use(async (context, next) =>
            {
                if (!context.Request.IsHttps && !context.Request.Path.StartsWithSegments("/healthz"))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { title = "HTTPS is required." });
                    return;
                }
                await next(context);
            });
            app.UseHsts();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/healthz").AllowAnonymous();
        app.MapTopicEndpoints();
        return app;
    }
}

/// <summary>Migrates the store on startup, then removes expired subscriptions and old copies on a timer.</summary>
internal sealed class TopicStoreMaintenance(ITopicStore store, TopicServerOptions options, ILogger<TopicStoreMaintenance> logger)
    : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Before the server takes requests, so the tables exist.
        if (options.MigrateOnStartup) await store.MigrateAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await store.CleanupAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Topic cleanup failed; it runs again in {Interval}", options.CleanupInterval);
            }
        }
    }
}

internal sealed class TopicStoreHealthCheck(ITopicStore store) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.PingAsync(cancellationToken)
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("The topic store did not answer.");
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("The topic store cannot be reached.", ex);
        }
    }
}
