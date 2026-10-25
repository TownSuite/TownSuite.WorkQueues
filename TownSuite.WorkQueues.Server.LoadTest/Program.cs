// Load test for TownSuite.WorkQueues.Server. Publishers post to a topic while subscribers long-poll
// and ack. With --baseline, every scenario in the file is run and checked against its thresholds;
// the exit code is 1 on any regression, so a CI stage fails. See run_loadtest.sh.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TownSuite.WorkQueues.Client;

var cli = Cli.Parse(args);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

Baseline baseline = cli.BaselinePath != null
    ? JsonSerializer.Deserialize<Baseline>(await File.ReadAllTextAsync(cli.BaselinePath), json)!
    : new Baseline { Scenarios = new() { cli.AdHocScenario() } };

var runner = new LoadRunner(cli.Url, cli.SigningKey);
await runner.WaitUntilHealthyAsync(TimeSpan.FromSeconds(60));

var results = new List<ScenarioResult>();
foreach (var scenario in baseline.Scenarios.Where(s => cli.Only == null || s.Name == cli.Only))
{
    Console.WriteLine($"=== {scenario.Name}: {scenario.Publishers} publishers, {scenario.Subscriptions} subscriptions x " +
                      $"{scenario.ReceiversPerSubscription} receivers, {scenario.DurationSeconds}s, {scenario.PayloadBytes}B");
    var measured = await runner.RunAsync(scenario);
    var failures = scenario.Thresholds?.Check(measured) ?? new List<string>();
    results.Add(new ScenarioResult(scenario.Name, scenario, measured, failures));

    Console.WriteLine(measured.Describe());
    Console.WriteLine(failures.Count == 0 ? "  PASS" : "  PERFORMANCE REGRESSION:\n" + string.Join("\n", failures.Select(f => "    - " + f)));
    Console.WriteLine();
}

if (cli.ReportPath != null)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cli.ReportPath))!);
        await File.WriteAllTextAsync(cli.ReportPath, JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, results }, json));
        Console.WriteLine($"Report written to {cli.ReportPath}");
    }
    catch (IOException ex)
    {
        // The results above still decide the exit code.
        Console.WriteLine($"Could not write the report to {cli.ReportPath}: {ex.Message}");
    }
}

var regressions = results.Where(r => r.Failures.Count > 0).Select(r => r.Name).ToList();
if (regressions.Count > 0)
{
    Console.WriteLine($"FAILED: performance regression in {string.Join(", ", regressions)} (thresholds in {cli.BaselinePath}).");
    return 1;
}
Console.WriteLine(results.Count == 0 ? "No scenarios ran." : "All scenarios within baseline.");
return results.Count == 0 ? 2 : 0;

// ── Baseline file ─────────────────────────────────────────────────────────

sealed class Baseline
{
    public string? Description { get; set; }
    public List<Scenario> Scenarios { get; set; } = new();
}

sealed class Scenario
{
    public string Name { get; set; } = "adhoc";
    public int Publishers { get; set; } = 4;
    public int Subscriptions { get; set; } = 2;
    public int ReceiversPerSubscription { get; set; } = 16;
    public int DurationSeconds { get; set; } = 30;
    public int PayloadBytes { get; set; } = 512;
    public int MaxPerReceive { get; set; } = 100;
    public Thresholds? Thresholds { get; set; }
}

sealed class Thresholds
{
    public double? MinPublishedPerSecond { get; set; }
    public double? MinDeliveredPerSecond { get; set; }
    public double? MaxPublishP99Ms { get; set; }
    public double? MaxEndToEndP99Ms { get; set; }
    public long MaxMissing { get; set; }
    public long MaxDuplicates { get; set; }
    public long MaxErrors { get; set; }

    public List<string> Check(Measured m)
    {
        var failures = new List<string>();
        void Below(string what, double actual, double? min) { if (min is { } v && actual < v) failures.Add($"{what} {actual:F0} is below the baseline minimum {v:F0}"); }
        void Above(string what, double actual, double? max) { if (max is { } v && actual > v) failures.Add($"{what} {actual:F1} is above the baseline maximum {v:F1}"); }

        Below("published msg/s", m.PublishedPerSecond, MinPublishedPerSecond);
        Below("delivered copies/s", m.DeliveredPerSecond, MinDeliveredPerSecond);
        Above("publish p99 ms", m.PublishLatency.P99, MaxPublishP99Ms);
        Above("end-to-end p99 ms", m.EndToEndLatency.P99, MaxEndToEndP99Ms);
        Above("missing copies", m.Missing, MaxMissing);
        Above("duplicate copies", m.Duplicates, MaxDuplicates);
        Above("errors", m.Errors.Values.Sum(), MaxErrors);
        return failures;
    }
}

sealed record ScenarioResult(string Name, Scenario Settings, Measured Measured, List<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

sealed record Latency(double P50, double P95, double P99, double Max)
{
    public static Latency From(IEnumerable<double> samples)
    {
        var v = samples.ToArray();
        if (v.Length == 0) return new(0, 0, 0, 0);
        Array.Sort(v);
        double P(double p) => v[Math.Min(v.Length - 1, (int)(p * v.Length))];
        return new(Math.Round(P(0.50), 1), Math.Round(P(0.95), 1), Math.Round(P(0.99), 1), Math.Round(v[^1], 1));
    }

    public override string ToString() => $"p50={P50,7:F1}  p95={P95,7:F1}  p99={P99,7:F1}  max={Max,8:F1} ms";
}

sealed record Measured(long Published, double PublishSeconds, double PublishedPerSecond, long ExpectedCopies, long Delivered,
    double DeliveredPerSecond, long Missing, long Duplicates, Latency PublishLatency, Latency EndToEndLatency, Latency AckLatency,
    Dictionary<string, int> Errors)
{
    public string Describe() =>
        $"  published   {Published} in {PublishSeconds:F1}s = {PublishedPerSecond:F0} msg/s\n" +
        $"  delivered   {Delivered} of {ExpectedCopies} copies = {DeliveredPerSecond:F0}/s, missing {Missing}, duplicates {Duplicates}\n" +
        $"  publish     {PublishLatency}\n" +
        $"  end-to-end  {EndToEndLatency}\n" +
        $"  ack         {AckLatency}\n" +
        $"  errors      {(Errors.Count == 0 ? "none" : string.Join(", ", Errors.Select(e => $"{e.Key} x{e.Value}")))}";
}

// ── Runner ────────────────────────────────────────────────────────────────

sealed class LoadRunner(string url, string signingKey)
{
    private readonly SocketsHttpHandler _handler = new() { MaxConnectionsPerServer = 2000, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    private readonly string _token = Jwt.Create(signingKey, "https://auth.loadtest", "workqueues", "publish:* subscribe:*");

    private HttpClient Http() => new(_handler, disposeHandler: false) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };

    private WorkQueuesClient NewClient() =>
        new(Http(), new WorkQueuesClientOptions { AccessTokenProvider = _ => ValueTask.FromResult<string?>(_token) });

    public async Task WaitUntilHealthyAsync(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        using var http = Http();
        while (true)
        {
            try
            {
                if ((await http.GetAsync("healthz")).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) when (deadline.Elapsed < timeout) { }
            if (deadline.Elapsed >= timeout) throw new TimeoutException($"{url}healthz did not report healthy within {timeout}.");
            await Task.Delay(500);
        }
    }

    public async Task<Measured> RunAsync(Scenario sc)
    {
        var topic = $"load-{Guid.NewGuid():N}"[..20];
        var client = NewClient();
        var subs = Enumerable.Range(0, sc.Subscriptions).Select(i => $"sub{i}").ToArray();
        foreach (var s in subs) await client.SubscribeAsync(topic, s);

        var publishLatency = new ConcurrentBag<double>();
        var e2eLatency = new ConcurrentBag<double>();
        var ackLatency = new ConcurrentBag<double>();
        var errors = new ConcurrentDictionary<string, int>();
        long expectedCopies = 0, published = 0;
        var received = subs.ToDictionary(s => s, _ => new ConcurrentDictionary<Guid, int>());
        var pad = new string('x', Math.Max(0, sc.PayloadBytes - 80));
        void Error(Exception ex) => errors.AddOrUpdate(ex is WorkQueuesHttpException h ? $"HTTP {(int)h.StatusCode}" : ex.GetType().Name, 1, (_, n) => n + 1);

        using var stopReceivers = new CancellationTokenSource();
        var receivers = subs.SelectMany(s => Enumerable.Range(0, sc.ReceiversPerSubscription).Select(_ => Task.Run(async () =>
        {
            var c = NewClient();
            while (!stopReceivers.IsCancellationRequested)
            {
                try
                {
                    var batch = await c.ReceiveAsync<LoadMessage>(topic, s, TimeSpan.FromSeconds(2), sc.MaxPerReceive, stopReceivers.Token);
                    foreach (var m in batch)
                    {
                        e2eLatency.Add(Stopwatch.GetElapsedTime(m.Message.SentTicks).TotalMilliseconds);
                        received[s].AddOrUpdate(m.MessageId, 1, (_, n) => n + 1);
                        var ackStart = Stopwatch.GetTimestamp();
                        await c.AckAsync(topic, s, m.Receipt, stopReceivers.Token);
                        ackLatency.Add(Stopwatch.GetElapsedTime(ackStart).TotalMilliseconds);
                    }
                }
                catch (OperationCanceledException) when (stopReceivers.IsCancellationRequested) { }
                catch (Exception ex) { Error(ex); await Task.Delay(100); }
            }
        }))).ToArray();

        var run = Stopwatch.StartNew();
        var until = TimeSpan.FromSeconds(sc.DurationSeconds);
        var publishers = Enumerable.Range(0, sc.Publishers).Select(_ => Task.Run(async () =>
        {
            var c = NewClient();
            while (run.Elapsed < until)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    var r = await c.PublishAsync(topic, new LoadMessage { SentTicks = start, Pad = pad });
                    publishLatency.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    Interlocked.Add(ref expectedCopies, r.Subscriptions);
                    Interlocked.Increment(ref published);
                }
                catch (Exception ex) { Error(ex); }
            }
        })).ToArray();
        await Task.WhenAll(publishers);
        var publishSeconds = run.Elapsed.TotalSeconds;

        // Drain: until every expected copy has arrived, or nothing new arrives for 15 s.
        long last = -1;
        var idle = Stopwatch.StartNew();
        while (true)
        {
            long got = received.Values.Sum(d => d.Count);
            if (got >= Interlocked.Read(ref expectedCopies)) break;
            if (got != last) { last = got; idle.Restart(); }
            if (idle.Elapsed > TimeSpan.FromSeconds(15)) break;
            await Task.Delay(250);
        }
        var totalSeconds = run.Elapsed.TotalSeconds;
        stopReceivers.Cancel();
        try { await Task.WhenAll(receivers); } catch (OperationCanceledException) { }

        foreach (var s in subs)
        {
            try { await client.UnsubscribeAsync(topic, s); } catch (Exception ex) { Error(ex); }
        }

        long delivered = received.Values.Sum(d => d.Count);
        return new Measured(published, Math.Round(publishSeconds, 1), Math.Round(published / publishSeconds), expectedCopies, delivered,
            Math.Round(delivered / totalSeconds), expectedCopies - delivered, received.Values.Sum(d => d.Values.Sum(n => n - 1L)),
            Latency.From(publishLatency), Latency.From(e2eLatency), Latency.From(ackLatency), new Dictionary<string, int>(errors));
    }
}

public sealed class LoadMessage
{
    public long SentTicks { get; set; }
    public string Pad { get; set; } = "";
}

// ── Command line ──────────────────────────────────────────────────────────

sealed record Cli(string Url, string SigningKey, string? BaselinePath, string? ReportPath, string? Only, string[] Args)
{
    public static Cli Parse(string[] a)
    {
        string? Get(string name) { var i = Array.IndexOf(a, "--" + name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        return new(Get("url") ?? "http://localhost:8080/",
            Get("key") ?? Environment.GetEnvironmentVariable("LOADTEST_SIGNING_KEY") ?? "load-test-signing-key-that-is-at-least-32-bytes",
            Get("baseline"), Get("report"), Get("scenario"), a);
    }

    // Without --baseline: one scenario from --publishers, --subs, --receivers, --duration, --payload, --max.
    public Scenario AdHocScenario()
    {
        int Get(string name, int def) { var i = Array.IndexOf(Args, "--" + name); return i >= 0 && i + 1 < Args.Length ? int.Parse(Args[i + 1]) : def; }
        return new Scenario
        {
            Publishers = Get("publishers", 4), Subscriptions = Get("subs", 2), ReceiversPerSubscription = Get("receivers", 16),
            DurationSeconds = Get("duration", 30), PayloadBytes = Get("payload", 512), MaxPerReceive = Get("max", 100)
        };
    }
}

static class Jwt
{
    // HS256 token, built by hand so the load test needs no extra packages.
    public static string Create(string key, string issuer, string audience, string scope)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = issuer, aud = audience, sub = "load-test", scope, nbf = now - 60, iat = now, exp = now + 3600 }));
        var sig = B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{header}.{body}")));
        return $"{header}.{body}.{sig}";
    }
}
