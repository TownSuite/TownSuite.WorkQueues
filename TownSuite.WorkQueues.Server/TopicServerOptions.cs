namespace TownSuite.WorkQueues.Server;

/// <summary>Configuration for the server, bound from the <c>WorkQueuesServer</c> section.</summary>
public sealed class TopicServerOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "WorkQueuesServer";

    /// <summary>
    /// Where topics are stored: <c>sqlserver</c>, <c>postgres</c> or <c>redis</c>. Use Redis for higher
    /// volumes. <c>memory</c> keeps everything in the process and loses it on restart: for local
    /// development and tests only.
    /// </summary>
    public string Backend { get; set; } = "postgres";

    /// <summary>Connection string for the SQL Server or Postgres database, or the Redis server.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Schema for the <c>topicsubscription</c> and <c>topicdelivery</c> tables. Defaults to <c>public</c>
    /// on Postgres and <c>dbo</c> on SQL Server.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>Prefix for every Redis key the server uses.</summary>
    public string KeyPrefix { get; set; } = "wqserver";

    /// <summary>Create or upgrade the SQL Server or Postgres tables on startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Largest message body accepted, in bytes. Larger requests get 413. Defaults to 256 KB.</summary>
    public int MaxMessageSize { get; set; } = 256 * 1024;

    /// <summary>
    /// A subscription that is neither renewed nor polled for this long expires: it stops receiving
    /// copies and its waiting messages are removed. Defaults to five minutes.
    /// </summary>
    public TimeSpan SubscriptionIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a received message is held for its receiver before it is delivered again. Defaults to 30 seconds.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Deliveries before an unacknowledged message is dead-lettered. Defaults to 5.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Delay before a negatively acknowledged message is delivered again, grown by
    /// <see cref="RetryBackoffMultiplier"/> per attempt and capped at <see cref="MaxRetryDelay"/>.
    /// A message whose lease expires is delivered again when the lease ends. On Redis the delay is
    /// approximate and never longer than <see cref="ClaimLease"/>.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Multiplier applied to <see cref="RetryDelay"/> for each further attempt. Defaults to 2.</summary>
    public double RetryBackoffMultiplier { get; set; } = 2.0;

    /// <summary>Cap on the computed retry delay. Defaults to five minutes.</summary>
    public TimeSpan? MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Longest long-poll a receive may ask for. Defaults to 30 seconds.</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Most messages one receive may return. Defaults to 100.</summary>
    public int MaxMessagesPerReceive { get; set; } = 100;

    /// <summary>How often a waiting receive checks the store for new messages. Defaults to 500 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often expired subscriptions and old messages are removed. Defaults to one minute.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long acknowledged messages are kept (SQL Server and Postgres). Defaults to one day.</summary>
    public TimeSpan RetainAcknowledged { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long dead-lettered messages are kept. Defaults to seven days.</summary>
    public TimeSpan RetainDeadLettered { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Reject requests that did not arrive over HTTPS (400). Behind a TLS-terminating proxy, enable
    /// <see cref="TrustForwardedHeaders"/> so <c>X-Forwarded-Proto</c> is honoured. <c>/healthz</c> is exempt.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Honour <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c> from the ingress or load balancer.</summary>
    public bool TrustForwardedHeaders { get; set; } = true;

    /// <summary>Bearer token validation.</summary>
    public JwtSettings Jwt { get; set; } = new();

    internal TimeSpan GetRetryDelay(int attempt) => new BatchOptions
    {
        RetryDelay             = RetryDelay,
        RetryBackoffMultiplier = RetryBackoffMultiplier,
        MaxRetryDelay          = MaxRetryDelay
    }.GetRetryDelay(attempt);
}

/// <summary>How bearer tokens are validated, and which claim grants topic access.</summary>
public sealed class JwtSettings
{
    /// <summary>
    /// OpenID Connect authority whose discovery document supplies the signing keys and issuer.
    /// Set this, or <see cref="SigningKey"/> and <see cref="Issuer"/>.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>Required <c>iss</c>. Taken from the authority's discovery document when <see cref="Authority"/> is set and this is not.</summary>
    public string? Issuer { get; set; }

    /// <summary>Required <c>aud</c>.</summary>
    public string? Audience { get; set; }

    /// <summary>Symmetric (HMAC) signing key, at least 32 bytes, when there is no <see cref="Authority"/>.</summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// The claim that lists topic permissions, as <c>publish:{topic}</c> and <c>subscribe:{topic}</c>
    /// (or <c>publish:*</c> / <c>subscribe:*</c> for every topic). Values may be space-separated in one
    /// claim, as OAuth <c>scope</c> is, or repeated claims. Defaults to <c>scope</c>.
    /// </summary>
    public string TopicClaimType { get; set; } = "scope";

    /// <summary>Require the authority's metadata to be fetched over HTTPS.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
