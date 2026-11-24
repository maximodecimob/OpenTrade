using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTrade.Api;

namespace OpenTrade.LoadTests;

/// <summary>Settings of the server under test. Rate limits and the key cap default to the real app's values.</summary>
public sealed record ServerOptions(int Port = 5099, int LatencyMs = 0, int V1PerMinute = 120, int GlobalPerMinute = 600, int LoginPerMinute = 5, int MaxKeys = 50)
{
    public const string AdminEmail = "loadtest-admin@example.com", AdminPassword = "load-test-admin-password";

    public string[] ToArgs() => ["server", "--port", $"{Port}", "--latency", $"{LatencyMs}", "--v1", $"{V1PerMinute}", "--global", $"{GlobalPerMinute}", "--login", $"{LoginPerMinute}", "--keys", $"{MaxKeys}"];

    public static ServerOptions Parse(string[] a)
    {
        int Get(string name, int def) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? int.Parse(a[i + 1], CultureInfo.InvariantCulture) : def; }
        return new(Get("--port", 5099), Get("--latency", 0), Get("--v1", 120), Get("--global", 600), Get("--login", 5), Get("--keys", 50));
    }

    public string Describe() => $"broker latency {LatencyMs} ms, /v1 limit {V1PerMinute}/min per key, global limit {GlobalPerMinute}/min per IP, login {LoginPerMinute}/min, max {MaxKeys} API keys";
}

/// <summary>
/// The real OpenTrade app (same Program.cs, same middleware, same SQLite database) on a real Kestrel socket, with Alpaca replaced by <see cref="SimBroker"/>.
/// It runs in its own process, so the CPU and memory we measure belong to the server only and not to the load generator.
/// </summary>
sealed class AppUnderTest(ServerOptions o, string dataDir) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Development"); // what `dotnet run` uses (launchSettings.json)
        b.UseContentRoot(RepoRoot()); // the app lives in the repository root (OpenTrade.csproj, wwwroot/, appsettings.json)
        b.UseSetting("Data:Dir", dataDir);
        b.UseSetting("Admin:Email", ServerOptions.AdminEmail);
        b.UseSetting("Admin:Password", ServerOptions.AdminPassword);
        b.UseSetting("RateLimit:V1PerMinute", $"{o.V1PerMinute}");
        b.UseSetting("RateLimit:GlobalPerMinute", $"{o.GlobalPerMinute}");
        b.UseSetting("RateLimit:LoginPerMinute", $"{o.LoginPerMinute}");
        b.UseSetting("Keys:MaxCount", $"{o.MaxKeys}");
        b.UseSetting("Logging:LogLevel:Default", "Error");
        b.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Error");
        b.ConfigureServices(s =>
        {
            foreach (var d in s.Where(x => x.ServiceType == typeof(IBrokerFactory)).ToList()) s.Remove(d);
            s.AddSingleton<IBrokerFactory>(new SimFactory(o.LatencyMs));
        });
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "OpenTrade.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("OpenTrade.csproj not found above " + AppContext.BaseDirectory);
    }
}

static class ServerMode
{
    public static async Task<int> Run(ServerOptions o)
    {
        var dir = Path.Combine(Path.GetTempPath(), "opentrade-load-" + Guid.NewGuid().ToString("N")[..8]);
        using var app = new AppUnderTest(o, dir);
        app.UseKestrel(o.Port);
        app.StartServer();
        Console.WriteLine($"READY {o.Port}");

        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(() => Sample(dir, cts.Token));
        // The parent closes our stdin (or dies) when it is done: that is the signal to stop.
        await Task.Run(() => { while (Console.ReadLine() is { } line && line != "quit") { } });
        cts.Cancel();
        try { await sampler; } catch (OperationCanceledException) { }
        app.Dispose();
        try { Directory.Delete(dir, true); } catch { /* sqlite may still hold the file for a moment */ }
        return 0;
    }

    /// <summary>Prints one STATS line every 250 ms: CPU (% of the whole machine), memory, GC and thread pool, database size.</summary>
    static async Task Sample(string dir, CancellationToken ct)
    {
        var p = Process.GetCurrentProcess();
        var cores = Environment.ProcessorCount;
        var lastCpu = p.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        var db = Path.Combine(dir, "opentrade.db");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct);
            p.Refresh();
            var cpu = p.TotalProcessorTime;
            var wall = Stopwatch.GetElapsedTime(lastWall);
            lastWall = Stopwatch.GetTimestamp();
            var pct = (cpu - lastCpu).TotalMilliseconds / wall.TotalMilliseconds / cores * 100;
            lastCpu = cpu;
            long dbBytes = 0;
            foreach (var f in new[] { db, db + "-wal", db + "-journal" }) if (File.Exists(f)) dbBytes += new FileInfo(f).Length;
            var gc = GC.GetGCMemoryInfo();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"STATS {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {pct:0.0} {p.WorkingSet64 / 1048576.0:0.0} {p.PrivateMemorySize64 / 1048576.0:0.0} {gc.HeapSizeBytes / 1048576.0:0.0} {p.Threads.Count} {ThreadPool.ThreadCount} {ThreadPool.PendingWorkItemCount} {GC.CollectionCount(0)} {GC.CollectionCount(2)} {dbBytes / 1048576.0:0.00}"));
        }
    }
}
