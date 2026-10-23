using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TownSuite.WorkQueues.Client;

namespace TownSuite.WorkQueues.Server.Testing;

/// <summary>The HTTP API and the client, over the in-memory backend.</summary>
[TestFixture]
public class TopicApiTests
{
    private const string Issuer = "https://auth.test";
    private const string Audience = "workqueues";
    private const string SigningKey = "test-signing-key-that-is-at-least-32-bytes-long";

    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            // UseSetting applies before Program reads configuration.
            host.UseSetting("WorkQueuesServer:Backend", "memory");
            host.UseSetting("WorkQueuesServer:MaxMessageSize", "1024");
            host.UseSetting("WorkQueuesServer:ClaimLease", "00:00:30");
            host.UseSetting("WorkQueuesServer:RetryDelay", "00:00:00.200");
            host.UseSetting("WorkQueuesServer:PollInterval", "00:00:00.050");
            host.UseSetting("WorkQueuesServer:Jwt:Issuer", Issuer);
            host.UseSetting("WorkQueuesServer:Jwt:Audience", Audience);
            host.UseSetting("WorkQueuesServer:Jwt:SigningKey", SigningKey);
        });
    }

    [OneTimeTearDown]
    public void StopServer() => _factory.Dispose();

    private static string Topic() => $"t{Guid.NewGuid():N}";

    private static string Token(string scope, string audience = Audience, string signingKey = SigningKey) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer             = Issuer,
            Audience           = audience,
            Subject            = new ClaimsIdentity(new[] { new Claim("sub", "test-service"), new Claim("scope", scope) }),
            Expires            = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256)
        });

    private HttpClient Http(string? token = null, bool https = true)
    {
        var http = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(https ? "https://localhost/" : "http://localhost/"),
            AllowAutoRedirect = false
        });
        if (token != null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private WorkQueuesClient Client(string scope = "publish:* subscribe:*") =>
        new(Http(), new WorkQueuesClientOptions
        {
            AccessTokenProvider = _ => ValueTask.FromResult<string?>(Token(scope)),
            LongPollWait        = TimeSpan.FromSeconds(2),
            ReconnectDelay      = TimeSpan.FromMilliseconds(100)
        });

    // ── Security ──────────────────────────────────────────────────────────────

    [Test]
    public async Task NoOrInvalidToken_Is401()
    {
        var topic = Topic();
        using var anonymous = Http();
        Assert.That((await anonymous.PostAsJsonAsync($"api/v1/topics/{topic}/messages", new { a = 1 })).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));

        using var wrongAudience = Http(Token("publish:*", audience: "someone-else"));
        Assert.That((await wrongAudience.PostAsJsonAsync($"api/v1/topics/{topic}/messages", new { a = 1 })).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));

        using var wrongKey = Http(Token("publish:*", signingKey: "another-signing-key-that-is-at-least-32-bytes"));
        Assert.That((await wrongKey.PutAsync($"api/v1/topics/{topic}/subscriptions/s", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TokenWithoutTheTopicClaim_Is403()
    {
        var topic = Topic();
        using var subscribeOnly = Http(Token($"subscribe:{topic}"));
        Assert.That((await subscribeOnly.PostAsJsonAsync($"api/v1/topics/{topic}/messages", new { a = 1 })).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden), "subscribe does not grant publish.");
        Assert.That((await subscribeOnly.PutAsync($"api/v1/topics/{topic}/subscriptions/s", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        using var otherTopic = Http(Token("publish:other subscribe:other"));
        Assert.That((await otherTopic.PostAsJsonAsync($"api/v1/topics/{topic}/messages", new { a = 1 })).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await otherTopic.GetAsync($"api/v1/topics/{topic}/subscriptions/s/messages")).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));

        using var exact = Http(Token($"publish:{topic}"));
        Assert.That((await exact.PostAsJsonAsync($"api/v1/topics/{topic}/messages", new { a = 1 })).StatusCode,
            Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task PlainHttp_IsRejected_ExceptHealth()
    {
        using var http = Http(Token("publish:*"), https: false);
        var response = await http.PostAsJsonAsync($"api/v1/topics/{Topic()}/messages", new { a = 1 });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("HTTPS"));

        Assert.That((await http.GetAsync("healthz")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "Probes use plain HTTP.");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Test]
    public async Task Publish_RejectsOversizedInvalidJsonAndBadNames()
    {
        using var http = Http(Token("publish:*"));
        var topic = Topic();

        var big = await http.PostAsync($"api/v1/topics/{topic}/messages",
            new StringContent($"\"{new string('x', 2000)}\"", Encoding.UTF8, "application/json"));
        Assert.That(big.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));

        var notJson = await http.PostAsync($"api/v1/topics/{topic}/messages", new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.That(notJson.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var badName = await http.PostAsJsonAsync("api/v1/topics/-bad/messages", new { a = 1 });
        Assert.That(badName.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        Assert.Throws<ArgumentException>(() => Client().PublishAsync("bad name", new { }).GetAwaiter().GetResult());
    }

    // ── Publish / subscribe ───────────────────────────────────────────────────

    [Test]
    public async Task TwoSubscribers_EachReceiveTheirOwnCopy()
    {
        var topic = Topic();
        var client = Client();
        await client.SubscribeAsync(topic, "billing");
        await client.SubscribeAsync(topic, "cache");

        var published = await client.PublishAsync(topic, new CacheInvalidated { Key = "fees" });
        Assert.That(published.Subscriptions, Is.EqualTo(2));

        foreach (var sub in new[] { "billing", "cache" })
        {
            var received = await client.ReceiveAsync<CacheInvalidated>(topic, sub);
            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0].Message.Key, Is.EqualTo("fees"));
            Assert.That(received[0].MessageId, Is.EqualTo(published.MessageId));
            Assert.That(received[0].Attempt, Is.EqualTo(1));
            Assert.That(await client.AckAsync(topic, sub, received[0].Receipt), Is.True);
            Assert.That(await client.AckAsync(topic, sub, received[0].Receipt), Is.False, "Second ack: already done.");
        }
    }

    [Test]
    public async Task Receive_LongPollsUntilAMessageArrives()
    {
        var topic = Topic();
        var client = Client();
        await client.SubscribeAsync(topic, "s");

        var stopwatch = Stopwatch.StartNew();
        var receive = client.ReceiveAsync<CacheInvalidated>(topic, "s", wait: TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        await client.PublishAsync(topic, new CacheInvalidated { Key = "late" });
        var received = await receive;
        stopwatch.Stop();

        Assert.That(received.Single().Message.Key, Is.EqualTo("late"));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "Returns as soon as the message arrives.");

        stopwatch.Restart();
        Assert.That(await client.ReceiveAsync<CacheInvalidated>(topic, "s", wait: TimeSpan.FromSeconds(1)), Is.Empty);
        Assert.That(stopwatch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)), "Waits out the poll when empty.");
    }

    [Test]
    public async Task Receive_OnUnknownSubscription_Throws()
    {
        var client = Client();
        Assert.ThrowsAsync<SubscriptionNotFoundException>(() => client.ReceiveAsync<CacheInvalidated>(Topic(), "nobody"));
    }

    [Test]
    public async Task GetSubscription_ReportsBacklog()
    {
        var topic = Topic();
        var client = Client();
        await client.SubscribeAsync(topic, "s");
        await client.PublishAsync(topic, new CacheInvalidated { Key = "a" });
        await client.PublishAsync(topic, new CacheInvalidated { Key = "b" });

        using var http = Http(Token("subscribe:*"));
        var stats = await http.GetFromJsonAsync<Dictionary<string, object>>($"api/v1/topics/{topic}/subscriptions/s");
        Assert.That(stats!["pending"].ToString(), Is.EqualTo("2"));
        Assert.That((await http.GetAsync($"api/v1/topics/{topic}/subscriptions/none")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task RunSubscription_AcksHandledMessages_RetriesFailures_AndReregisters()
    {
        var topic = Topic();
        var client = Client();
        var handled = new ConcurrentQueue<(string Key, int Attempt)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var loop = client.RunSubscriptionAsync<CacheInvalidated>(topic, "worker", (m, _) =>
        {
            handled.Enqueue((m.Message.Key, m.Attempt));
            if (m.Message.Key == "flaky" && m.Attempt == 1) throw new InvalidOperationException("first try fails");
            return Task.CompletedTask;
        }, unsubscribeOnStop: true, cancellationToken: stop.Token);

        await WaitFor(async () => (await client.PublishAsync(topic, new CacheInvalidated { Key = "probe" })).Subscriptions == 1);
        await client.PublishAsync(topic, new CacheInvalidated { Key = "ok" });
        await client.PublishAsync(topic, new CacheInvalidated { Key = "flaky" });
        await WaitFor(() => Task.FromResult(handled.Count(h => h.Key is "ok" or "flaky") >= 3));

        Assert.That(handled.Where(h => h.Key == "ok").Select(h => h.Attempt), Is.EqualTo(new[] { 1 }), "Acknowledged once handled.");
        Assert.That(handled.Where(h => h.Key == "flaky").Select(h => h.Attempt), Is.EqualTo(new[] { 1, 2 }), "Nacked, then redelivered.");

        // The subscription disappears (as if it expired): the loop registers it again and carries on.
        await client.UnsubscribeAsync(topic, "worker");
        await WaitFor(async () => (await client.PublishAsync(topic, new CacheInvalidated { Key = "after" })).Subscriptions == 1);
        await WaitFor(() => Task.FromResult(handled.Any(h => h.Key == "after")));
        Assert.That(handled.Any(h => h.Key == "after"), Is.True);

        stop.Cancel();
        await loop;
        Assert.That((await client.PublishAsync(topic, new CacheInvalidated { Key = "gone" })).Subscriptions, Is.Zero,
            "unsubscribeOnStop removed the subscription.");
    }

    [Test]
    public async Task Publish_CarriesThePublishersTraceToSubscribers()
    {
        var topic = Topic();
        var client = Client();
        await client.SubscribeAsync(topic, "s");

        const string traceId = "0af7651916cd43dd8448eb211c80319c";
        using var http = Http(Token("publish:*"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/topics/{topic}/messages")
        {
            Content = JsonContent.Create(new CacheInvalidated { Key = "traced" })
        };
        request.Headers.Add("traceparent", $"00-{traceId}-b7ad6b7169203331-01");
        Assert.That((await http.SendAsync(request)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var received = (await client.ReceiveAsync<CacheInvalidated>(topic, "s")).Single();
        Assert.That(received.TraceParent, Does.StartWith($"00-{traceId}-"), "The subscriber sees the publisher's trace id.");
    }

    private static async Task WaitFor(Func<Task<bool>> predicate, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }
}

public class CacheInvalidated
{
    public string Key { get; set; } = "";
}
