using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenTrade.Bots;

/// <summary>Creates the example bots in OpenTrade (with one API key each) using an admin login, and saves the keys.</summary>
public static class Setup
{
    public sealed record Result(string Name, string Status);

    public static async Task<List<Result>> Run(HttpClient http, string email, string password, IEnumerable<IBot> bots, decimal allocationPct,
        string? brokerName, string keysPath, Action<string> log)
    {
        async Task<JsonElement> Call(HttpMethod m, string path, object? body = null)
        {
            using var req = new HttpRequestMessage(m, path);
            req.Headers.Add("X-OT", "1");
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                string msg;
                try { msg = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString() ?? res.ReasonPhrase ?? ""; } catch { msg = res.ReasonPhrase ?? ""; }
                throw new InvalidOperationException($"{m} {path} failed: HTTP {(int)res.StatusCode} {msg}");
            }
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }

        await Call(HttpMethod.Post, "api/login", new { email, password });
        var brokers = (await Call(HttpMethod.Get, "api/brokers")).EnumerateArray().ToList();
        if (brokers.Count == 0) throw new InvalidOperationException("No broker is connected yet. Open OpenTrade > Brokers > Connect broker first.");
        var broker = brokerName is null ? brokers[0] : brokers.FirstOrDefault(b => b.GetProperty("name").GetString() == brokerName);
        if (broker.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Broker not found. Available: " + string.Join(", ", brokers.Select(b => b.GetProperty("name").GetString())));
        var brokerId = broker.GetProperty("id").GetString()!;
        log($"Using broker account: {broker.GetProperty("name").GetString()} ({(broker.GetProperty("paper").GetBoolean() ? "paper" : "LIVE")})");

        var existing = (await Call(HttpMethod.Get, "api/bots")).GetProperty("bots").EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToHashSet();
        var keys = File.Exists(keysPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(keysPath)) ?? [] : [];
        var results = new List<Result>();
        foreach (var bot in bots)
        {
            if (existing.Contains(bot.Name))
            {
                results.Add(new(bot.Name, keys.ContainsKey(bot.Name) ? "already exists" : "already exists (no saved key - rotate it in API Management)"));
                continue;
            }
            var created = await Call(HttpMethod.Post, "api/bots", new { name = bot.Name, @class = "US equities", description = bot.Description, brokerId, allocationPct });
            var key = await Call(HttpMethod.Post, "api/keys", new
            {
                name = bot.Name + " key", brokerId, botId = created.GetProperty("id").GetString(), perms = new[] { "read", "positions", "trade" }, expiresInDays = 365,
            });
            keys[bot.Name] = key.GetProperty("secret").GetString()!;
            results.Add(new(bot.Name, $"created with {allocationPct:0.##}% of the account"));
        }
        File.WriteAllText(keysPath, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        return results;
    }

    public static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is var k && k.Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    public static HttpClient Admin(string url) => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
    {
        BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30),
    };
}
