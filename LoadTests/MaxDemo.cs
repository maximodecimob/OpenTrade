using System.Diagnostics;
using System.Globalization;

namespace OpenTrade.LoadTests;

/// <summary>
/// The short demo (about 1-2 minutes): "what is the maximum traffic OpenTrade handles?"
/// It adds users (each with a browser tab and N bots) in short steps until the server can no longer keep up, then gives everything it has
/// in one last burst. At the end it prints the maximum it sustained, the maximum burst, and what that cost in CPU and memory.
/// </summary>
public static class MaxDemo
{
    public sealed record Options(int[] Users, int Bots, int Seconds);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task Run(Options o)
    {
        var total = Stopwatch.StartNew();
        const string sc = "MAX: how much traffic OpenTrade handles";
        // The limits are raised so we measure the server and not the rate limiter: the bots pace themselves at 100 req/min (under the
        // real 120/min per key), but the burst has no pauses at all, and the 50-key cap would stop the test at 50 bots.
        // The limits themselves are tested by the "limits" scenario.
        var opts = new ServerOptions(Port: 5120, LatencyMs: 30, V1PerMinute: 100_000_000, GlobalPerMinute: 100_000_000, MaxKeys: 1_000_000);
        Console.WriteLine();
        Console.WriteLine("==========================================================================================");
        Console.WriteLine($" OpenTrade MAXIMUM TRAFFIC test   ({Environment.ProcessorCount} logical cores, {o.Seconds} s per step)");
        Console.WriteLine($" Each user = 1 browser tab (dashboard, orders, bots pages) + {o.Bots} bots.");
        Console.WriteLine(" Each bot  = 100 requests/min: bot status, prices, positions, open orders, PLACE order, CANCEL order.");
        Console.WriteLine(" Broker    = simulated Alpaca, 30 ms per call.   Rate limits raised (we measure the server, not the limiter).");
        Console.WriteLine(" A step is OK when the server keeps up: >= 90% of the target rate, p95 < 1 s, no errors.");
        Console.WriteLine("==========================================================================================");
        Report.Quiet = true; // this demo prints its own short lines
        await using var server = await ServerProcess.Start(opts);
        Report.Note($"[{sc}] server: {opts.Describe()}");
        var seed = new Seeder(server);
        await seed.LoginAdmin();
        var users = new List<SimUser>();
        StepResult? best = null, last = null;
        var rows = new List<(StepResult R, double Target, double Orders, bool Ok)>();
        foreach (var target in o.Users)
        {
            var sw = Stopwatch.StartNew();
            users.AddRange(await seed.Users(users.Count, target, o.Bots));
            Console.WriteLine($"\n  -> {target} users × {o.Bots} bots = {target * o.Bots} bots   (created in {sw.Elapsed.TotalSeconds:0.0} s, running {o.Seconds} s...)");
            var r = await Scenarios.RunFleet(server, sc, users, TimeSpan.FromSeconds(o.Seconds));
            var bots = users.Sum(u => u.Bots.Count);
            var targetRps = bots * 100 / 60.0 + users.Count * 30 / 60.0;
            var orders = r.Breakdown.Where(b => b.Tag.Contains("/v1/orders") && !b.Tag.StartsWith("GET")).Sum(b => b.N) / r.Seconds;
            var errors = r.Codes.Where(c => !c.Key.StartsWith('2')).Sum(c => c.Value);
            var ok = r.OkRps >= 0.9 * targetRps && r.P95 < 1000 && errors == 0;
            rows.Add((r, targetRps, orders, ok));
            Console.WriteLine(string.Create(Inv, $"     {(ok ? "OK  " : "SLOW")}  {r.OkRps,6:0} req/s of {targetRps:0} wanted   p95 {r.P95,6:0} ms   orders {orders,5:0}/s   CPU {r.CpuAvg,3:0}% (peak {r.CpuPeak:0}%)   RAM {r.WsPeak:0} MB"));
            last = r;
            if (ok) best = r;
            else break;
        }

        // Last step: no pauses at all. Every bot key in turn, as fast as the server answers: the raw maximum.
        var keys = users.SelectMany(u => u.Bots.Select(b => (b.Key, u.Ip))).ToArray();
        Console.WriteLine($"\n  -> BURST: 256 connections, no pauses, rotating over {keys.Length} bot keys ({Math.Min(10, o.Seconds)} s)...");
        var url = server.BaseUrl;
        var burst = await Measure.Closed(server, sc, "BURST (no pauses)", 256, TimeSpan.FromSeconds(Math.Min(10, o.Seconds)), (w, i, rec) =>
        {
            var (key, ip) = keys[(w * 7919 + i) % keys.Length];
            return (i % 3) switch
            {
                0 => rec.Send(Net.Req(HttpMethod.Get, url + "/v1/bot", ip, key)),
                1 => rec.Send(Net.Req(HttpMethod.Get, url + "/v1/positions", ip, key)),
                _ => rec.Send(Net.Req(HttpMethod.Get, url + "/v1/orders?status=open&limit=20", ip, key)),
            };
        });

        Console.WriteLine();
        Console.WriteLine("==========================================================================================");
        Console.WriteLine(" RESULT");
        Console.WriteLine("------------------------------------------------------------------------------------------");
        Console.WriteLine("  Users × bots         req/s (wanted)    p95 ms   orders/s   CPU avg/peak   RAM     ");
        foreach (var (r, t, ord, ok) in rows)
            Console.WriteLine(string.Create(Inv, $"  {r.Step,-20} {r.OkRps,6:0} ({t,5:0})    {r.P95,7:0}   {ord,7:0}    {r.CpuAvg,3:0}% / {r.CpuPeak,3:0}%    {r.WsPeak,5:0} MB  {(ok ? "OK" : "too slow")}"));
        Console.WriteLine("------------------------------------------------------------------------------------------");
        if (best is not null)
        {
            var b = rows.First(x => x.R == best);
            Console.WriteLine(string.Create(Inv, $"  MAXIMUM SUSTAINED : {best.Step} -> {best.OkRps:0} requests/s ({best.OkRps * 60:0} per minute), p95 {best.P95:0} ms"));
            Console.WriteLine(string.Create(Inv, $"                      of which {b.Orders:0} order operations/s (place + cancel), CPU {best.CpuAvg:0}% avg, RAM {best.WsPeak:0} MB"));
            if (last != best) Console.WriteLine($"  NEXT STEP          : {last!.Step} was too slow -> the limit is between these two.");
            else Console.WriteLine("  (every step was OK: the maximum is higher; try more users with --users)");
        }
        else Console.WriteLine("  Even the first step was too slow on this machine (try --users 5,10,20).");
        Console.WriteLine(string.Create(Inv, $"  MAXIMUM BURST     : {burst.OkRps:0} answered requests/s (all {burst.OkPct:0}% OK), p95 {burst.P95:0} ms, CPU {burst.CpuAvg:0}% avg / {burst.CpuPeak:0}% peak, RAM {burst.WsPeak:0} MB"));
        Console.WriteLine(string.Create(Inv, $"  (CPU = % of all {Environment.ProcessorCount} cores, server process only. The load generator runs on the same PC and used {burst.ClientCpu:0}% during the burst.)"));
        Console.WriteLine($"  Total test time   : {total.Elapsed.TotalSeconds:0} s");
        Console.WriteLine("==========================================================================================");
    }

    public static Options Parse(string[] args, Func<string, int, int> opt)
    {
        var i = Array.IndexOf(args, "--users");
        int[] users = i >= 0 && i + 1 < args.Length ? [.. args[i + 1].Split(',').Select(x => int.Parse(x, Inv))] : [10, 50, 100, 150, 200, 300];
        return new(users, opt("--bots", 10), opt("--seconds", 10));
    }
}
