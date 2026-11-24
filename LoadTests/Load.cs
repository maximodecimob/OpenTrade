using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpenTrade.LoadTests;

public sealed record Sample(long T, double Cpu, double WorkingSetMb, double PrivateMb, double HeapMb, int Threads, int PoolThreads, long Queue, int Gen0, int Gen2, double DbMb);

/// <summary>Starts the server under test as a child process and collects its STATS lines.</summary>
public sealed class ServerProcess : IAsyncDisposable
{
    readonly Process _p;
    readonly ConcurrentQueue<Sample> _samples = new();
    public ServerOptions Options { get; }
    public string BaseUrl => $"http://localhost:{Options.Port}";

    ServerProcess(Process p, ServerOptions o) { _p = p; Options = o; }

    public static async Task<ServerProcess> Start(ServerOptions o)
    {
        var exe = Environment.ProcessPath!;
        var args = o.ToArgs().ToList();
        // Started with `dotnet LoadTests.dll`: run the dll again. Started with the apphost (LoadTests.exe): run the exe.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) args.Insert(0, Assembly.GetEntryAssembly()!.Location);
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var sp = new ServerProcess(p, o);
        var ready = new TaskCompletionSource();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("READY", StringComparison.Ordinal)) ready.TrySetResult();
            else if (e.Data.StartsWith("STATS ", StringComparison.Ordinal)) sp._samples.Enqueue(ParseSample(e.Data));
            else if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine("  [server] " + e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine("  [server!] " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var first = await Task.WhenAny(ready.Task, p.WaitForExitAsync(), Task.Delay(60_000));
        if (first != ready.Task) throw new Exception(p.HasExited ? "the server process exited (see [server!] lines above)" : "server did not start in 60 s");
        await Task.Delay(1000); // let the first samples (idle baseline) arrive
        return sp;
    }

    static Sample ParseSample(string line)
    {
        var f = line.Split(' ');
        double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
        int I(int i) => int.Parse(f[i], CultureInfo.InvariantCulture);
        return new(long.Parse(f[1], CultureInfo.InvariantCulture), D(2), D(3), D(4), D(5), I(6), I(7), long.Parse(f[8], CultureInfo.InvariantCulture), I(9), I(10), D(11));
    }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Sample[] Between(long from, long to) => [.. _samples.Where(s => s.T >= from && s.T <= to)];
    /// <summary>The samples closest in time to the window (one before and one after), for windows shorter than the sampling interval.</summary>
    public Sample[] Around(long from, long to)
    {
        var all = _samples.ToArray();
        var before = all.LastOrDefault(x => x.T < from);
        var after = all.FirstOrDefault(x => x.T > to);
        return [.. new[] { before, after }.OfType<Sample>()];
    }

    public Sample Latest => _samples.LastOrDefault() ?? new Sample(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public async ValueTask DisposeAsync()
    {
        try { await _p.StandardInput.WriteLineAsync("quit"); _p.StandardInput.Close(); } catch { }
        if (!_p.WaitForExit(15_000)) _p.Kill(true);
        _p.Dispose();
    }
}

/// <summary>
/// One shared HTTP client for every simulated user and bot. Each simulated user gets its own IP address through X-Forwarded-For:
/// OpenTrade trusts that header from the local machine (a reverse proxy), so the per-IP limits (web interface, wrong keys) apply per simulated user, as they would in production.
/// </summary>
public static class Net
{
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false, MaxConnectionsPerServer = 4096,
        // The server listens on 127.0.0.1; connect there directly instead of trying ::1 first (the Host header stays "localhost", which AllowedHosts requires).
        ConnectCallback = async (ctx, ct) =>
        {
            var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ctx.DnsEndPoint.Port), ct); return new System.Net.Sockets.NetworkStream(s, true); }
            catch { s.Dispose(); throw; }
        }, PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(10),
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public static string Ip(int n) => $"10.{n / 62500 % 250}.{n / 250 % 250}.{n % 250 + 1}";

    public static HttpRequestMessage Req(HttpMethod m, string url, string? ip = null, string? key = null, string? cookie = null, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (ip is not null) r.Headers.Add("X-Forwarded-For", ip);
        if (key is not null) r.Headers.Add("Authorization", "Bearer " + key);
        if (cookie is not null) r.Headers.Add("Cookie", cookie);
        if (!m.Equals(HttpMethod.Get)) r.Headers.Add("X-OT", "1");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    public static async Task<JsonElement> Json(HttpRequestMessage req, HttpStatusCode expect = HttpStatusCode.OK)
    {
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != expect) throw new Exception($"{req.Method} {req.RequestUri} -> {(int)res.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Signs in and returns the session cookie ("ot.sid=...").</summary>
    public static async Task<string> Login(string baseUrl, string email, string password, string ip)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Post, baseUrl + "/api/login", ip, body: new { email, password }));
        if (!res.IsSuccessStatusCode) throw new Exception($"login {email} -> {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var set = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ot.sid=", StringComparison.Ordinal));
        return set[..set.IndexOf(';')];
    }
}

/// <summary>Latencies and status codes of one worker. Each worker owns its recorder, so recording needs no locks.</summary>
public sealed class Recorder
{
    public readonly List<double> Ms = new(4096);
    public readonly Dictionary<string, int> Codes = [];
    /// <summary>Latencies per endpoint (tag), for the breakdown tables.</summary>
    public readonly Dictionary<string, List<double>> ByTag = [];
    public int Count => Ms.Count;

    public void Add(double ms, string code, string? tag = null)
    {
        Ms.Add(ms);
        Codes[code] = Codes.GetValueOrDefault(code) + 1;
        if (tag is null) return;
        if (!ByTag.TryGetValue(tag, out var l)) ByTag[tag] = l = [];
        l.Add(ms);
    }

    /// <summary>Sends the request, records how long it took and the status code (or "timeout"/"error"). Returns the status code (0 on failure).</summary>
    public async Task<(int Status, string Body)> Send(HttpRequestMessage req, bool readBody = false, string? tag = null)
    {
        var t = Stopwatch.GetTimestamp();
        try
        {
            using var res = await Net.Http.SendAsync(req);
            var body = readBody ? await res.Content.ReadAsStringAsync() : "";
            if (!readBody) await res.Content.CopyToAsync(Stream.Null);
            Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds, ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture), tag);
            return ((int)res.StatusCode, body);
        }
        catch (TaskCanceledException) { Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds, "timeout", tag); return (0, ""); }
        catch (HttpRequestException) { Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds, "error", tag); return (0, ""); }
        finally { req.Dispose(); }
    }
}

public sealed record StepResult(string Scenario, string Step, string Load, int Requests, double Seconds, double Rps, double OkRps,
    double P50, double P95, double P99, double Max, Dictionary<string, int> Codes, double CpuAvg, double CpuPeak, double WsPeak, double HeapPeak, int ThreadsPeak, double DbMb, double ClientCpu)
{
    /// <summary>Per endpoint: count, p50, p95, p99.</summary>
    public List<(string Tag, int N, double P50, double P95, double P99)> Breakdown { get; init; } = [];
    public double OkPct => Requests == 0 ? 0 : 100.0 * Codes.Where(c => c.Key.StartsWith('2')).Sum(c => c.Value) / Requests;
    public string CodeText => string.Join(" ", Codes.OrderByDescending(c => c.Value).Select(c => $"{c.Key}×{c.Value}"));
}

public static class Measure
{
    public static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];

    public static StepResult Summarize(string scenario, string step, string load, IEnumerable<Recorder> recs, long from, long to, ServerProcess server, double clientCpu)
    {
        var all = recs.SelectMany(r => r.Ms).ToList();
        all.Sort();
        var codes = new Dictionary<string, int>();
        foreach (var r in recs) foreach (var (k, v) in r.Codes) codes[k] = codes.GetValueOrDefault(k) + v;
        var secs = (to - from) / 1000.0;
        var ok = codes.Where(c => c.Key.StartsWith('2')).Sum(c => c.Value);
        // A burst shorter than the 250 ms sampling interval may contain no sample: then use the samples just around it.
        var s = server.Between(from, to + 300);
        if (s.Length == 0) s = server.Around(from, to);
        var tags = recs.SelectMany(r => r.ByTag).GroupBy(kv => kv.Key).Select(g =>
        {
            var l = g.SelectMany(kv => kv.Value).ToList(); l.Sort();
            return (g.Key, l.Count, Pct(l, 50), Pct(l, 95), Pct(l, 99));
        }).OrderByDescending(x => x.Item4).ToList();
        return new(scenario, step, load, all.Count, secs, all.Count / secs, ok / secs, Pct(all, 50), Pct(all, 95), Pct(all, 99), all.Count == 0 ? 0 : all[^1], codes,
            s.Length == 0 ? 0 : s.Average(x => x.Cpu), s.Length == 0 ? 0 : s.Max(x => x.Cpu), s.Length == 0 ? 0 : s.Max(x => x.WorkingSetMb),
            s.Length == 0 ? 0 : s.Max(x => x.HeapMb), s.Length == 0 ? 0 : s.Max(x => x.Threads), server.Latest.DbMb, clientCpu) { Breakdown = tags };
    }

    /// <summary>CPU used by this (load generator) process while <paramref name="work"/> runs, in % of the machine.</summary>
    public static async Task<double> ClientCpu(Func<Task> work)
    {
        var p = Process.GetCurrentProcess();
        var c0 = p.TotalProcessorTime;
        var t0 = Stopwatch.GetTimestamp();
        await work();
        p.Refresh();
        return (p.TotalProcessorTime - c0).TotalMilliseconds / Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Environment.ProcessorCount * 100;
    }

    /// <summary>Closed loop: <paramref name="concurrency"/> workers send requests back to back for <paramref name="duration"/>. Finds the maximum throughput.</summary>
    public static async Task<StepResult> Closed(ServerProcess server, string scenario, string step, int concurrency, TimeSpan duration,
        Func<int, int, Recorder, Task> call)
    {
        var recs = Enumerable.Range(0, concurrency).Select(_ => new Recorder()).ToArray();
        long from = 0, to = 0;
        var cpu = await ClientCpu(async () =>
        {
            from = ServerProcess.Now;
            var end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(w => Task.Run(async () =>
            {
                for (var i = 0; Stopwatch.GetTimestamp() < end; i++) await call(w, i, recs[w]);
            })));
            to = ServerProcess.Now;
        });
        var r = Summarize(scenario, step, $"{concurrency} parallel", recs, from, to, server, cpu);
        Report.Print(r);
        return r;
    }
}

/// <summary>Collects every step and writes the Markdown report.</summary>
public static class Report
{
    public static readonly List<StepResult> All = [];
    public static readonly List<string> Notes = [];

    /// <summary>True: record steps for the report but do not print them (the max demo prints its own lines).</summary>
    public static bool Quiet;

    public static void Print(StepResult r)
    {
        lock (All) All.Add(r);
        if (Quiet) return;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {r.Step,-34} {r.Load,-22} {r.Rps,8:0} req/s  ok {r.OkPct,5:0.0}%  p50 {r.P50,7:0.0}  p95 {r.P95,7:0.0}  p99 {r.P99,7:0.0} ms  cpu {r.CpuAvg,4:0}% (peak {r.CpuPeak:0})  ram {r.WsPeak,5:0} MB  {r.CodeText}"));
        foreach (var b in r.Breakdown) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {b.Tag,-36} n {b.N,7}  p50 {b.P50,7:0.0}  p95 {b.P95,7:0.0}  p99 {b.P99,7:0.0} ms"));
    }

    public static void Note(string s) { lock (Notes) Notes.Add(s); Console.WriteLine("  > " + s); }

    public static string Write(string title)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine($"- Machine: {Environment.ProcessorCount} logical cores, {Environment.OSVersion}, .NET {Environment.Version}");
        sb.AppendLine("- CPU is % of the whole machine (100% = all cores busy). RAM is the server's peak working set. Latencies are measured by the client, in ms.").AppendLine();
        foreach (var g in All.GroupBy(r => r.Scenario))
        {
            sb.AppendLine($"## {g.Key}").AppendLine();
            sb.AppendLine("| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (var r in g)
                sb.AppendLine(string.Create(inv, $"| {r.Step} | {r.Load} | {r.Requests} | {r.Rps:0} | {r.OkRps:0} | {r.OkPct:0.0} | {r.P50:0.0} | {r.P95:0.0} | {r.P99:0.0} | {r.Max:0} | {r.CpuAvg:0}% / {r.CpuPeak:0}% | {r.WsPeak:0} MB | {r.HeapPeak:0} MB | {r.ThreadsPeak} | {r.DbMb:0.0} | {r.ClientCpu:0}% | {r.CodeText} |"));
            sb.AppendLine();
            foreach (var r in g.Where(r => r.Breakdown.Count > 0))
            {
                sb.AppendLine($"**{r.Step}**, per endpoint:").AppendLine().AppendLine("| Endpoint | Requests | p50 | p95 | p99 |").AppendLine("|---|---:|---:|---:|---:|");
                foreach (var b in r.Breakdown) sb.AppendLine(string.Create(inv, $"| {b.Tag} | {b.N} | {b.P50:0.0} | {b.P95:0.0} | {b.P99:0.0} |"));
                sb.AppendLine();
            }
        }
        if (Notes.Count > 0) { sb.AppendLine("## Notes").AppendLine(); foreach (var n in Notes) sb.AppendLine("- " + n); }
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results");
        Directory.CreateDirectory(dir);
        var file = Path.GetFullPath(Path.Combine(dir, $"load-test-{DateTime.Now:yyyyMMdd-HHmmss}.md"));
        File.WriteAllText(file, sb.ToString());
        File.WriteAllText(Path.Combine(dir, "latest.md"), sb.ToString());
        return file;
    }
}
