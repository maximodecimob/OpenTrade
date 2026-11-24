using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OpenTrade.LoadTests;

public sealed record RunOptions(bool Quick, int MaxUsers, int BotsPerUser)
{
    public TimeSpan Step => TimeSpan.FromSeconds(Quick ? 3 : 8);
    public TimeSpan FleetStep => TimeSpan.FromSeconds(Quick ? 10 : 30);
}

public static class Scenarios
{
    static readonly string[] Symbols = ["AAPL", "MSFT", "SPY", "QQQ", "NVDA"];
    static int _orderSeq;
    static string ClientId(string prefix) => $"{prefix}-{Interlocked.Increment(ref _orderSeq):D8}-{Guid.NewGuid():N}"[..40];
    static int Code(string c) => int.Parse(c, CultureInfo.InvariantCulture);

    // =====================================================================================================
    /// <summary>1. Are the limits real? With the default configuration, count how many requests get through and when 429 starts.</summary>
    public static async Task Limits(RunOptions o)
    {
        const string sc = "1. Rate limits (default configuration)";
        Console.WriteLine("\n== " + sc);
        var opts = new ServerOptions(Port: 5101);
        await using var server = await ServerProcess.Start(opts);
        Report.Note($"[{sc}] server: {opts.Describe()}");
        var seed = new Seeder(server);
        await seed.LoginAdmin();
        var broker = await seed.Broker("LIMITS0001");
        var bots = new List<SimBot>();
        for (var i = 0; i < 16; i++) bots.Add(await seed.Bot(broker, 1, "lim" + i));
        var url = server.BaseUrl;

        async Task<StepResult> Burst(string step, int total, int parallel, Func<int, HttpRequestMessage> make)
        {
            var recs = Enumerable.Range(0, parallel).Select(_ => new Recorder()).ToArray();
            long from = 0, to = 0;
            var next = -1;
            var cpu = await Measure.ClientCpu(async () =>
            {
                from = ServerProcess.Now;
                await Task.WhenAll(Enumerable.Range(0, parallel).Select(w => Task.Run(async () =>
                {
                    int i;
                    while ((i = Interlocked.Increment(ref next)) < total) await recs[w].Send(make(i));
                })));
                to = ServerProcess.Now;
            });
            var r = Measure.Summarize(sc, step, $"{total} req, {parallel} parallel", recs, from, to, server, cpu);
            Report.Print(r);
            return r;
        }
        int Ok(StepResult r) => r.Codes.Where(c => c.Key.StartsWith('2')).Sum(c => c.Value);

        // A: one bot (one key) on one machine sends 200 requests at once: the per-key limit lets 120 through.
        var a = await Burst("A. one key, one IP", 200, 10, _ => Net.Req(HttpMethod.Get, url + "/v1/bot", Net.Ip(1), bots[0].Key));
        Report.Note($"A: one API key got {Ok(a)} of 200 requests through (expected 120 = RateLimit:V1PerMinute), the rest were 429.");

        // B: 7 bots on the SAME machine (same IP): the bot API is limited per key only, so each bot gets its own 120 (840 together).
        var b = await Burst("B. 7 keys, same IP", 900, 20, i => Net.Req(HttpMethod.Get, url + "/v1/bot", Net.Ip(2), bots[1 + i % 7].Key));
        Report.Note($"B: 7 keys behind ONE IP got {Ok(b)} of 900 through (expected 840 = 7 × 120: the per-IP limit does not apply to bots).");

        // C: the same 7 bots on 7 different machines: now each one gets its own 120.
        var c = await Burst("C. 7 keys, 7 IPs", 1050, 20, i => Net.Req(HttpMethod.Get, url + "/v1/bot", Net.Ip(10 + i % 7), bots[8 + i % 7].Key));
        Report.Note($"C: 7 keys on 7 different IPs got {Ok(c)} of 1050 through (expected 840 = 7 × 120).");

        // F: made-up API keys from one IP: 30 per minute, then 429 (so guessing keys cannot get around the per-key limit).
        var f = await Burst("F. wrong API keys, one IP", 40, 1, i => Net.Req(HttpMethod.Get, url + "/v1/bot", Net.Ip(70), "ot_test_made-up-key-" + i));
        Report.Note($"F: 40 made-up API keys from one IP: {f.CodeText} (expected 30×401 then 429 = RateLimit:BadKeyPerMinute).");

        // D: brute force on the login: 5 tries per minute per IP, then 429 (and every try costs a full PBKDF2 hash).
        var d = await Burst("D. wrong logins, one IP", 12, 1, _ => Net.Req(HttpMethod.Post, url + "/api/login", Net.Ip(50), body: new { email = ServerOptions.AdminEmail, password = "wrong-password-123" }));
        Report.Note($"D: 12 wrong passwords from one IP: {d.CodeText} (expected 5×401 then 429).");

        // E: the fixed window has an edge. 120 just before the minute ends + 120 just after = ~240 in about 2 seconds for ONE key.
        if (!o.Quick)
        {
            var key = bots[15].Key;
            var ip = Net.Ip(60);
            var first = new Recorder();
            await first.Send(Net.Req(HttpMethod.Get, url + "/v1/bot", ip, key)); // opens the key's window
            var t0 = Stopwatch.GetTimestamp();
            Console.WriteLine("  (waiting ~58 s for the end of the rate-limit window...)");
            await Task.Delay(TimeSpan.FromSeconds(58));
            var before = await Burst("E1. window edge, before", 119, 10, _ => Net.Req(HttpMethod.Get, url + "/v1/bot", ip, key));
            while (Stopwatch.GetElapsedTime(t0) < TimeSpan.FromSeconds(60.5)) await Task.Delay(50);
            var after = await Burst("E2. window edge, after", 150, 10, _ => Net.Req(HttpMethod.Get, url + "/v1/bot", ip, key));
            var secs = Stopwatch.GetElapsedTime(t0).TotalSeconds - 58;
            Report.Note(string.Create(CultureInfo.InvariantCulture,
                $"E: fixed-window edge: one key got {Ok(before) + Ok(after) + 1} requests through in about {secs:0.0} s around the window change (the 'limit' of 120/min is per window, not per any 60 s)."));
        }
    }

    // =====================================================================================================
    /// <summary>
    /// 2. Many users, each with many bots, with the real rate limits. Every user has a broker account, an open browser tab (the dashboard
    /// polling like the React app does) and N bots that each call the API at 100 requests/min (just under the 120 limit) doing what a real bot does.
    /// We add users until the server cannot keep up.
    /// </summary>
    public static async Task Fleet(RunOptions o)
    {
        const string sc = "2. Fleet: many users × many bots (real rate limits)";
        Console.WriteLine("\n== " + sc);
        // Real limits; only the 50-key cap is raised, because 50 keys = 50 bots for the whole server is itself the first limit we hit.
        var opts = new ServerOptions(Port: 5102, LatencyMs: 30, MaxKeys: 100_000);
        await using var server = await ServerProcess.Start(opts);
        Report.Note($"[{sc}] server: {opts.Describe()}. Each bot: 100 req/min in a loop of 6 calls (bot, bars, positions, open orders, place limit order, cancel it). Each user: 1 browser tab (dashboard every 10 s, orders + bots every 5 s) = ~30 req/min.");
        var seed = new Seeder(server);
        await seed.LoginAdmin();
        var users = new List<SimUser>();
        int[] steps = [.. new[] { 10, 25, 50, 100, 200, 400, 800 }.Where(n => n <= o.MaxUsers)];
        foreach (var target in steps)
        {
            var sw = Stopwatch.StartNew();
            users.AddRange(await seed.Users(users.Count, target, o.BotsPerUser));
            Console.WriteLine($"  seeded {users.Count} users × {o.BotsPerUser} bots in {sw.Elapsed.TotalSeconds:0.0} s");
            var r = await RunFleet(server, sc, users, o.FleetStep);
            if (target == 10 && o.BotsPerUser == 5) Report.Note("Step 10 users × 5 bots = 50 bots is the largest fleet the DEFAULT configuration allows (Keys:MaxCount = 50 API keys in total).");
            if (r.P95 > 2000 || r.OkPct < 95) { Report.Note($"[{sc}] stopped the ramp at {target} users: p95 {r.P95:0} ms, OK {r.OkPct:0.0}%."); break; }
        }
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public static async Task<StepResult> RunFleet(ServerProcess server, string sc, List<SimUser> users, TimeSpan duration)
    {
        var url = server.BaseUrl;
        var botInterval = TimeSpan.FromMilliseconds(600); // 100 req/min
        var recs = new List<Recorder>();
        var tasks = new List<Func<Task>>();
        var end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var rnd = new Random(42);
        foreach (var u in users)
        {
            foreach (var bot in u.Bots)
            {
                var rec = new Recorder(); recs.Add(rec);
                var offset = TimeSpan.FromMilliseconds(rnd.Next(600));
                var sym = Symbols[rnd.Next(Symbols.Length)];
                tasks.Add(async () =>
                {
                    await Task.Delay(offset);
                    var start = Stopwatch.GetTimestamp();
                    string? lastOrder = null;
                    for (var i = 0; Stopwatch.GetTimestamp() < end; i++)
                    {
                        switch (i % 6)
                        {
                            case 0: await rec.Send(Net.Req(HttpMethod.Get, url + "/v1/bot", u.Ip, bot.Key), tag: "GET /v1/bot"); break;
                            case 1: await rec.Send(Net.Req(HttpMethod.Get, $"{url}/v1/bars/{sym}?timeframe=1Day&limit=50", u.Ip, bot.Key), tag: "GET /v1/bars"); break;
                            case 2: await rec.Send(Net.Req(HttpMethod.Get, url + "/v1/positions", u.Ip, bot.Key), tag: "GET /v1/positions"); break;
                            case 3: await rec.Send(Net.Req(HttpMethod.Get, url + "/v1/orders?status=open&limit=20", u.Ip, bot.Key), tag: "GET /v1/orders?status=open"); break;
                            case 4:
                                var (st, body) = await rec.Send(Net.Req(HttpMethod.Post, url + "/v1/orders", u.Ip, bot.Key,
                                    body: new { symbol = sym, side = "buy", type = "limit", qty = 1, limitPrice = 50, clientOrderId = ClientId("f") }), readBody: true, tag: "POST /v1/orders");
                                lastOrder = st == 201 ? JsonDocument.Parse(body).RootElement.GetProperty("id").GetString() : null;
                                break;
                            default:
                                if (lastOrder is not null) await rec.Send(Net.Req(HttpMethod.Delete, $"{url}/v1/orders/{lastOrder}", u.Ip, bot.Key), tag: "DELETE /v1/orders/{id}");
                                else await rec.Send(Net.Req(HttpMethod.Post, url + "/v1/heartbeat", u.Ip, bot.Key), tag: "POST /v1/heartbeat");
                                break;
                        }
                        var due = start + (long)((i + 1) * botInterval.TotalSeconds * Stopwatch.Frequency);
                        var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Math.Min(due, end)); // never sleep past the end of the step
                        if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    }
                });
            }
            var web = new Recorder(); recs.Add(web);
            var webOffset = TimeSpan.FromMilliseconds(rnd.Next(5000));
            tasks.Add(async () =>
            {
                await Task.Delay(webOffset);
                for (var tick = 0; Stopwatch.GetTimestamp() < end; tick++)
                {
                    var t = Stopwatch.GetTimestamp();
                    if (tick % 2 == 0) await web.Send(Net.Req(HttpMethod.Get, url + "/api/dashboard", u.Ip, cookie: u.Cookie), tag: "GET /api/dashboard (web)");
                    await web.Send(Net.Req(HttpMethod.Get, url + "/api/orders?size=10&page=1&status=all", u.Ip, cookie: u.Cookie), tag: "GET /api/orders (web)");
                    await web.Send(Net.Req(HttpMethod.Get, url + "/api/bots", u.Ip, cookie: u.Cookie), tag: "GET /api/bots (web)");
                    var wait = Min(TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(t), Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), end));
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                }
            });
        }
        long from = 0, to = 0;
        var cpu = await Measure.ClientCpu(async () =>
        {
            from = ServerProcess.Now;
            await Task.WhenAll(tasks.Select(f => Task.Run(f)));
            to = ServerProcess.Now;
        });
        var bots = users.Sum(u => u.Bots.Count);
        var r = Measure.Summarize(sc, $"{users.Count} users × {users[0].Bots.Count} bots", $"{bots} bots + {users.Count} tabs (target {bots * 100 / 60 + users.Count * 30 / 60} req/s)", recs, from, to, server, cpu);
        Report.Print(r);
        return r;
    }

    // =====================================================================================================
    /// <summary>3. Raw ceiling: limits switched off, as many parallel requests as possible on the most used endpoints.</summary>
    public static async Task Ceiling(RunOptions o)
    {
        const string sc = "3. Raw ceiling (rate limits off, broker latency 0)";
        Console.WriteLine("\n== " + sc);
        var opts = new ServerOptions(Port: 5103, V1PerMinute: 100_000_000, GlobalPerMinute: 100_000_000, LoginPerMinute: 100_000_000, MaxKeys: 100_000);
        await using var server = await ServerProcess.Start(opts);
        Report.Note($"[{sc}] server: {opts.Describe()}");
        var seed = new Seeder(server);
        await seed.LoginAdmin();
        var users = await seed.Users(0, 4, 16); // 4 broker accounts, 64 bots
        var keys = users.SelectMany(u => u.Bots).Select(b => b.Key).ToArray();
        var url = server.BaseUrl;
        int[] levels = o.Quick ? [1, 16, 128] : [1, 8, 32, 128, 512];

        // warm-up (JIT, caches, connections)
        await Measure.Closed(server, "warm-up", "warm-up", 32, TimeSpan.FromSeconds(2), (w, i, r) => r.Send(Net.Req(HttpMethod.Get, url + "/v1/bot", Net.Ip(w), keys[w % keys.Length])));
        lock (Report.All) Report.All.RemoveAll(x => x.Scenario == "warm-up");

        foreach (var c in levels)
            await Measure.Closed(server, sc, "GET /v1/bot (key auth + DB)", c, o.Step, (w, i, r) => r.Send(Net.Req(HttpMethod.Get, url + "/v1/bot", Net.Ip(w), keys[w % keys.Length])));
        foreach (var c in levels)
            await Measure.Closed(server, sc, "GET /v1/bars (cached market data)", c, o.Step, (w, i, r) => r.Send(Net.Req(HttpMethod.Get, url + "/v1/bars/AAPL?limit=100", Net.Ip(w), keys[w % keys.Length])));
        foreach (var c in levels)
            await Measure.Closed(server, sc, "GET /api/dashboard (cookie)", c, o.Step, (w, i, r) => r.Send(Net.Req(HttpMethod.Get, url + "/api/dashboard", Net.Ip(w), cookie: users[w % users.Count].Cookie)));
        foreach (var c in levels)
            await Measure.Closed(server, sc, "GET /api/orders page (cookie)", c, o.Step, (w, i, r) => r.Send(Net.Req(HttpMethod.Get, url + "/api/orders?size=10&page=1&status=all", Net.Ip(w), cookie: users[w % users.Count].Cookie)));
        foreach (var c in o.Quick ? new[] { 1, 16 } : [1, 4, 16, 64])
            await Measure.Closed(server, sc, "POST /api/login (PBKDF2)", c, o.Step, (w, i, r) => r.Send(Net.Req(HttpMethod.Post, url + "/api/login", Net.Ip(w), body: new { email = users[w % users.Count].Email, password = Seeder.UserPassword })));
        Report.Note($"[{sc}] the load generator runs on the same machine and uses CPU too (column 'Client CPU'), so the server's real ceiling on a dedicated machine is higher.");
    }

    // =====================================================================================================
    /// <summary>4. Placing orders: how many order operations per second, and what limits them (per-bot lock, SQLite writes, broker latency, history size).</summary>
    public static async Task Orders(RunOptions o)
    {
        foreach (var latency in new[] { 0, 40 })
        {
            var sc = latency == 0 ? "4a. Orders, broker latency 0 ms" : "4b. Orders, broker latency 40 ms (like a real Alpaca round trip)";
            var full = latency == 0;
            Console.WriteLine("\n== " + sc);
            var opts = new ServerOptions(Port: 5104 + latency, LatencyMs: latency, V1PerMinute: 100_000_000, GlobalPerMinute: 100_000_000, LoginPerMinute: 100_000_000, MaxKeys: 100_000);
            await using var server = await ServerProcess.Start(opts);
            Report.Note($"[{sc}] server: {opts.Describe()}");
            var seed = new Seeder(server);
            await seed.LoginAdmin();
            var url = server.BaseUrl;
            var botSeq = 0;
            async Task<List<SimBot>> FreshBots(int n)
            {
                var broker = await seed.Broker($"ORD{latency:D2}{Interlocked.Increment(ref botSeq):D6}");
                var list = new List<SimBot>();
                for (var i = 0; i < n; i++) list.Add(await seed.Bot(broker, Math.Floor(100m / n * 100) / 100, $"o{latency}-{botSeq}-{i}"));
                return list;
            }
            HttpRequestMessage Buy(SimBot b, int w, string type = "market") => Net.Req(HttpMethod.Post, url + "/v1/orders", Net.Ip(w), b.Key,
                body: type == "market" ? new { symbol = "AAPL", side = "buy", type, qty = 1m, limitPrice = (decimal?)null, clientOrderId = ClientId("o") }
                                       : new { symbol = "AAPL", side = "buy", type, qty = 1m, limitPrice = (decimal?)50m, clientOrderId = ClientId("o") });

            var warm = await FreshBots(8);
            await Measure.Closed(server, "warm-up", "warm-up", 8, TimeSpan.FromSeconds(2), (w, i, r) => r.Send(Buy(warm[w], w)));
            lock (Report.All) Report.All.RemoveAll(x => x.Scenario == "warm-up");

            // Same bot from N parallel connections: every order of one bot waits for the previous one (lock for the capital check).
            foreach (var c in full ? (o.Quick ? new[] { 1, 16 } : [1, 4, 16, 64]) : [1, 16])
            {
                var bot = (await FreshBots(1))[0];
                await Measure.Closed(server, sc, "one bot, market buy", c, o.Step, (w, i, r) => r.Send(Buy(bot, w)));
            }
            // N different bots in parallel: no shared lock, only the database and the broker are shared.
            foreach (var c in full ? (o.Quick ? new[] { 4, 64 } : [1, 4, 16, 64, 256]) : [16, 64, 256])
            {
                var bots = await FreshBots(c);
                await Measure.Closed(server, sc, "N bots, market buy", c, o.Step, (w, i, r) => r.Send(Buy(bots[w], w)));
            }
            // History growth: one bot, one order after the other. The capital check reads ALL of the bot's orders each time.
            if (full)
            {
                var bot = (await FreshBots(1))[0];
                var total = o.Quick ? 2000 : 6000;
                var bucket = total / 6;
                for (var k = 0; k < total / bucket; k++)
                {
                    var rec = new Recorder();
                    var from = ServerProcess.Now;
                    var cpu = await Measure.ClientCpu(async () => { for (var i = 0; i < bucket; i++) await rec.Send(Buy(bot, 1)); });
                    Report.Print(Measure.Summarize(sc, "one bot's history grows", $"orders {k * bucket}-{(k + 1) * bucket}", [rec], from, ServerProcess.Now, server, cpu));
                }
            }
            if (full)
            {
                // Orders from the web UI (no bot): no capital check at all.
                foreach (var c in o.Quick ? new[] { 16 } : [1, 16, 64])
                {
                    var brokerId = await seed.Broker($"UI{c:D4}{Interlocked.Increment(ref botSeq):D6}");
                    await Measure.Closed(server, sc, "web UI order (no bot)", c, o.Step, (w, i, r) => r.Send(Net.Req(HttpMethod.Post, url + "/api/orders", Net.Ip(w), cookie: seed.AdminCookie,
                        body: new { brokerId, symbol = "AAPL", side = "buy", type = "market", qty = 1 })));
                }
                // Cancel: open limit orders on 64 bots, then cancel them all as fast as possible.
                {
                    var bots = await FreshBots(64);
                    var open = bots.Select(_ => new System.Collections.Concurrent.ConcurrentQueue<string>()).ToArray();
                    var placer = Enumerable.Range(0, 64).Select(_ => new Recorder()).ToArray();
                    await Task.WhenAll(Enumerable.Range(0, 64).Select(w => Task.Run(async () =>
                    {
                        for (var i = 0; i < (o.Quick ? 20 : 60); i++)
                        {
                            var (st, body) = await placer[w].Send(Buy(bots[w], w, "limit"), readBody: true);
                            if (st == 201) open[w].Enqueue(JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!);
                        }
                    })));
                    await Measure.Closed(server, sc, "cancel open order (64 bots)", 64, o.Step, async (w, i, r) =>
                    {
                        if (open[w].TryDequeue(out var id)) await r.Send(Net.Req(HttpMethod.Delete, $"{url}/v1/orders/{id}", Net.Ip(w), bots[w].Key));
                        else await Task.Delay(5);
                    });
                }
            }
        }
    }
}
