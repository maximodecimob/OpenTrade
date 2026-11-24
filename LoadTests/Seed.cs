using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace OpenTrade.LoadTests;

public sealed record SimBot(string Id, string Key);

/// <summary>A simulated customer: their own IP, web login, broker account (Alpaca account) and bots, each bot with its own API key.</summary>
public sealed record SimUser(int N, string Ip, string Email, string BrokerId, List<SimBot> Bots)
{
    public string Cookie { get; set; } = "";
}

/// <summary>Creates users, broker accounts, bots and keys through the real admin API (the same calls the web UI makes).</summary>
public sealed class Seeder(ServerProcess server)
{
    public const string UserPassword = "load-test-user-password";
    readonly string _url = server.BaseUrl;
    int _ipSeq = 200 * 62500; // seeding requests come from a separate IP range (10.200.x.x), so they do not eat the simulated users' limits
    public string AdminCookie { get; private set; } = "";

    string SeedIp() => Net.Ip(Interlocked.Increment(ref _ipSeq));

    public async Task LoginAdmin() => AdminCookie = await Net.Login(_url, ServerOptions.AdminEmail, ServerOptions.AdminPassword, SeedIp());

    Task<JsonElement> Admin(HttpMethod m, string path, object? body = null) => Net.Json(Net.Req(m, _url + path, SeedIp(), cookie: AdminCookie, body: body));

    public async Task<string> Broker(string tag)
    {
        var b = await Admin(HttpMethod.Post, "/api/brokers", new { name = "Sim " + tag, keyId = "SIM" + tag, secret = "sim-secret-" + tag + "-0000000000", paper = true });
        return b.GetProperty("id").GetString()!;
    }

    public async Task<SimBot> Bot(string brokerId, decimal pct, string tag)
    {
        var bot = await Admin(HttpMethod.Post, "/api/bots", new { name = "bot " + tag, @class = "LoadTest", brokerId, allocationPct = pct });
        var id = bot.GetProperty("id").GetString()!;
        var k = await Admin(HttpMethod.Post, "/api/keys", new { name = "key " + tag, brokerId, botId = id, perms = new[] { "read", "positions", "trade" }, expiresInDays = 30 });
        return new SimBot(id, k.GetProperty("secret").GetString()!);
    }

    /// <summary>Adds users <paramref name="from"/>..<paramref name="to"/>-1, each with one broker account and <paramref name="bots"/> bots, then signs each user in from their own IP.</summary>
    public async Task<List<SimUser>> Users(int from, int to, int bots)
    {
        var made = new ConcurrentBag<SimUser>();
        await Parallel.ForEachAsync(Enumerable.Range(from, to - from), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (n, _) =>
        {
            var tag = n.ToString("D6", CultureInfo.InvariantCulture);
            var email = $"user{tag}@load.test";
            await Admin(HttpMethod.Post, "/api/users", new { email, name = "User " + tag, role = "trader", password = UserPassword });
            var broker = await Broker("U" + tag);
            var list = new List<SimBot>();
            for (var i = 0; i < bots; i++) list.Add(await Bot(broker, Math.Floor(100m / bots), $"{tag}x{i}"));
            var u = new SimUser(n, Net.Ip(n), email, broker, list);
            u.Cookie = await Net.Login(_url, email, UserPassword, u.Ip);
            made.Add(u);
        });
        return [.. made.OrderBy(u => u.N)];
    }
}
