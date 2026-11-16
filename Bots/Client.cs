using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenTrade.Bots;

public sealed record BotSelf(string Id, string Name, bool Running, decimal Allocated);
public sealed record Holding(string Symbol, decimal Qty, decimal MarketValue);
public sealed record Candle(DateTimeOffset T, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
public sealed record PlacedOrder(string Id, string Symbol, string Status);
public sealed record OrderTicket(string Symbol, string Side, decimal Qty, string Type = "market", decimal? LimitPrice = null, string TimeInForce = "day");

public class ApiException(int status, string message) : Exception($"HTTP {status}: {message}")
{
    public int Status { get; } = status;
}

/// <summary>The bot was paused in the OpenTrade UI (HTTP 423). Orders are refused until it is resumed.</summary>
public sealed class BotPausedException(string message) : ApiException(423, message);

/// <summary>What the bots need from OpenTrade. A fake implements it in tests.</summary>
public interface IOpenTradeClient
{
    Task<BotSelf> GetBot(CancellationToken ct = default);
    Task<Dictionary<string, Holding>> GetPositions(CancellationToken ct = default);
    Task<List<PlacedOrder>> GetOpenOrders(CancellationToken ct = default);
    Task<List<Candle>> GetBars(string symbol, string timeframe, int limit, CancellationToken ct = default);
    Task<PlacedOrder> PlaceOrder(OrderTicket t, CancellationToken ct = default);
}

/// <summary>Talks to the OpenTrade /v1 API with an API key.</summary>
public sealed class OpenTradeClient : IOpenTradeClient
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http;

    /// <param name="http">Pass your own HttpClient (tests do); otherwise one is created for <paramref name="baseUrl"/>.</param>
    public OpenTradeClient(string baseUrl, string apiKey, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct, int retries = 2)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (HttpRequestException) when (retries > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return await Send<T>(method, path, body, ct, retries - 1);
        }
        catch (HttpRequestException e) { throw new ApiException(0, "cannot reach OpenTrade: " + e.Message); }
        using (res)
        {
            if (res.IsSuccessStatusCode) return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
            string msg;
            try { msg = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString() ?? res.ReasonPhrase ?? ""; }
            catch { msg = res.ReasonPhrase ?? "error"; }
            if (res.StatusCode == HttpStatusCode.TooManyRequests && retries > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);   // rate limited: wait and retry
                return await Send<T>(method, path, body, ct, retries - 1);
            }
            if (res.StatusCode == HttpStatusCode.Locked) throw new BotPausedException(msg);
            throw new ApiException((int)res.StatusCode, msg);
        }
    }

    public Task<BotSelf> GetBot(CancellationToken ct = default) => Send<BotSelf>(HttpMethod.Get, "v1/bot", null, ct);

    public async Task<Dictionary<string, Holding>> GetPositions(CancellationToken ct = default) =>
        (await Send<List<Holding>>(HttpMethod.Get, "v1/positions", null, ct)).ToDictionary(p => p.Symbol);

    public Task<List<PlacedOrder>> GetOpenOrders(CancellationToken ct = default) => Send<List<PlacedOrder>>(HttpMethod.Get, "v1/orders?status=open&limit=100", null, ct);

    public Task<List<Candle>> GetBars(string symbol, string timeframe, int limit, CancellationToken ct = default) =>
        Send<List<Candle>>(HttpMethod.Get, $"v1/bars/{Uri.EscapeDataString(symbol)}?timeframe={timeframe}&limit={limit}", null, ct);

    public Task<PlacedOrder> PlaceOrder(OrderTicket t, CancellationToken ct = default) => Send<PlacedOrder>(HttpMethod.Post, "v1/orders", t, ct);
}

/// <summary>Finds the API key of a bot: OT_KEY wins, otherwise the keys.json written by the setup command.</summary>
public static class KeyStore
{
    public static string Load(string botName, string? path = null)
    {
        if (Environment.GetEnvironmentVariable("OT_KEY") is { Length: > 0 } k) return k;
        path ??= Path.Combine(Directory.GetCurrentDirectory(), "keys.json");
        if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } d && d.TryGetValue(botName, out var key)) return key;
        throw new InvalidOperationException($"No API key for '{botName}'. Run the 'setup' command first, or set OT_KEY.");
    }
}
