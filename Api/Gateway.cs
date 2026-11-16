using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenTrade.Api;

public sealed record AccountInfo(string Number, string Status, decimal Equity, decimal LastEquity, decimal Cash, decimal BuyingPower);
public sealed record PositionInfo(string Symbol, decimal Qty, decimal AvgEntry, decimal Last, decimal MarketValue, decimal UnrealizedPl, decimal UnrealizedPlpc, string AssetClass);
public sealed record RemoteOrder(string Id, string? ClientOrderId, string Symbol, Side Side, OrderType Type, decimal Qty, decimal Filled,
    decimal? LimitPrice, decimal? AvgFill, OrderStatus Status, string Tif, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record NewOrder(string Symbol, Side Side, OrderType Type, decimal Qty, decimal? LimitPrice, string Tif, string ClientOrderId);
public sealed record AssetInfo(string Symbol, string Name, string Exchange, string Class, bool Tradable, bool Shortable, bool Fractionable);
public sealed record History(long[] T, double[] V);
public sealed record Bar(long T, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed class BrokerException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Everything the app needs from a broker. Alpaca is the implementation; tests use a fake.</summary>
public interface IBrokerGateway
{
    Task<AccountInfo> GetAccount(CancellationToken ct = default);
    Task<List<PositionInfo>> GetPositions(CancellationToken ct = default);
    Task<List<RemoteOrder>> GetOrders(CancellationToken ct = default);
    Task<RemoteOrder> PlaceOrder(NewOrder o, CancellationToken ct = default);
    Task CancelOrder(string brokerOrderId, CancellationToken ct = default);
    Task<int> CancelAllOrders(CancellationToken ct = default);
    Task ClosePosition(string symbol, CancellationToken ct = default);
    Task<List<AssetInfo>> GetAssets(CancellationToken ct = default);
    Task<History> GetHistory(string period, string timeframe, CancellationToken ct = default);
    /// <summary>Latest <paramref name="limit"/> price bars, oldest first. Timeframe: 1Min, 5Min, 15Min, 1Hour or 1Day.</summary>
    Task<List<Bar>> GetBars(string symbol, string timeframe, int limit, CancellationToken ct = default);
}

public interface IBrokerFactory
{
    IBrokerGateway Create(Broker broker, string secret);
}

public sealed class AlpacaFactory(IHttpClientFactory http, IConfiguration cfg) : IBrokerFactory
{
    public IBrokerGateway Create(Broker b, string secret)
    {
        var live = cfg["Alpaca:LiveUrl"] ?? "https://api.alpaca.markets";
        var paper = cfg["Alpaca:PaperUrl"] ?? "https://paper-api.alpaca.markets";
        return new AlpacaGateway(http.CreateClient("alpaca"), b.Paper ? paper : live, b.KeyId, secret, cfg["Alpaca:DataUrl"] ?? "https://data.alpaca.markets");
    }
}

public sealed class AlpacaGateway(HttpClient http, string baseUrl, string keyId, string secret, string dataUrl = "https://data.alpaca.markets") : IBrokerGateway
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    async Task<JsonDocument?> Send(HttpMethod method, string path, object? body, CancellationToken ct, bool data = false)
    {
        using var req = new HttpRequestMessage(method, data ? dataUrl.TrimEnd('/') + path : baseUrl.TrimEnd('/') + "/v2" + path);
        req.Headers.Add("APCA-API-KEY-ID", keyId);
        req.Headers.Add("APCA-API-SECRET-KEY", secret);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new BrokerException("Could not reach Alpaca: " + e.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BrokerException("Alpaca did not answer in time"); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var code = (int)res.StatusCode;
                if (code is 401 or 403) throw new BrokerException("Alpaca rejected the API credentials", 401);
                string? msg = null;
                try { msg = JsonDocument.Parse(text).RootElement.GetProperty("message").GetString(); } catch { /* body is not json */ }
                throw new BrokerException(msg ?? $"Alpaca error {code}", code is 422 or 404 ? code : 502);
            }
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
        }
    }

    static decimal Dec(JsonElement e, string name, decimal dflt = 0m) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Float, Inv, out var d) ? d : dflt,
            JsonValueKind.Number => p.GetDecimal(),
            _ => dflt,
        } : dflt;

    static decimal? DecN(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && (p.ValueKind is JsonValueKind.String or JsonValueKind.Number) ? Dec(e, name) : null;

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    static DateTimeOffset Time(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), Inv, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;

    public async Task<AccountInfo> GetAccount(CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, "/account", null, ct) ?? throw new BrokerException("Empty answer from Alpaca");
        var r = d.RootElement;
        return new(Str(r, "account_number"), Str(r, "status"), Dec(r, "equity"), Dec(r, "last_equity"), Dec(r, "cash"), Dec(r, "buying_power"));
    }

    public async Task<List<PositionInfo>> GetPositions(CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, "/positions", null, ct);
        return d is null ? [] : d.RootElement.EnumerateArray().Select(p => new PositionInfo(Str(p, "symbol"), Dec(p, "qty"), Dec(p, "avg_entry_price"),
            Dec(p, "current_price"), Dec(p, "market_value"), Dec(p, "unrealized_pl"), Dec(p, "unrealized_plpc"), Str(p, "asset_class"))).ToList();
    }

    static RemoteOrder ParseOrder(JsonElement o)
    {
        var type = Str(o, "type") is { Length: > 0 } t ? t : Str(o, "order_type");
        return new(Str(o, "id"), Str(o, "client_order_id"), Str(o, "symbol"), Str(o, "side") == "sell" ? Side.Sell : Side.Buy,
            type == "limit" ? OrderType.Limit : OrderType.Market, Dec(o, "qty"), Dec(o, "filled_qty"), DecN(o, "limit_price"), DecN(o, "filled_avg_price"),
            MapStatus(Str(o, "status")), Str(o, "time_in_force"), Time(o, "created_at"), Time(o, "updated_at"));
    }

    public static OrderStatus MapStatus(string s) => s switch
    {
        "filled" => OrderStatus.Filled,
        "partially_filled" => OrderStatus.Partial,
        "canceled" or "expired" or "done_for_day" or "replaced" => OrderStatus.Cancelled,
        "rejected" or "suspended" => OrderStatus.Rejected,
        _ => OrderStatus.Pending,
    };

    public async Task<List<RemoteOrder>> GetOrders(CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, "/orders?status=all&limit=500&direction=desc", null, ct);
        return d is null ? [] : d.RootElement.EnumerateArray().Select(ParseOrder).ToList();
    }

    public async Task<RemoteOrder> PlaceOrder(NewOrder o, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["symbol"] = o.Symbol,
            ["qty"] = o.Qty.ToString(Inv),
            ["side"] = o.Side == Side.Buy ? "buy" : "sell",
            ["type"] = o.Type == OrderType.Limit ? "limit" : "market",
            ["time_in_force"] = o.Tif,
            ["client_order_id"] = o.ClientOrderId,
        };
        if (o.Type == OrderType.Limit) body["limit_price"] = o.LimitPrice!.Value.ToString(Inv);
        using var d = await Send(HttpMethod.Post, "/orders", body, ct) ?? throw new BrokerException("Empty answer from Alpaca");
        return ParseOrder(d.RootElement);
    }

    public async Task CancelOrder(string id, CancellationToken ct = default) =>
        (await Send(HttpMethod.Delete, "/orders/" + Uri.EscapeDataString(id), null, ct))?.Dispose();

    public async Task<int> CancelAllOrders(CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Delete, "/orders", null, ct);
        return d is { RootElement.ValueKind: JsonValueKind.Array } ? d.RootElement.GetArrayLength() : 0;
    }

    public async Task ClosePosition(string symbol, CancellationToken ct = default) =>
        (await Send(HttpMethod.Delete, "/positions/" + Uri.EscapeDataString(symbol), null, ct))?.Dispose();

    public async Task<List<AssetInfo>> GetAssets(CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, "/assets?status=active", null, ct);
        static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
        return d is null ? [] : d.RootElement.EnumerateArray().Select(a => new AssetInfo(Str(a, "symbol"), Str(a, "name"), Str(a, "exchange"), Str(a, "class"),
            B(a, "tradable"), B(a, "shortable"), B(a, "fractionable"))).ToList();
    }

    public async Task<History> GetHistory(string period, string timeframe, CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, $"/account/portfolio/history?period={Uri.EscapeDataString(period)}&timeframe={Uri.EscapeDataString(timeframe)}", null, ct);
        if (d is null || !d.RootElement.TryGetProperty("timestamp", out var ts) || !d.RootElement.TryGetProperty("equity", out var eq)) return new([], []);
        var t = new List<long>();
        var v = new List<double>();
        var tl = ts.EnumerateArray().ToArray();
        var el = eq.EnumerateArray().ToArray();
        for (var i = 0; i < Math.Min(tl.Length, el.Length); i++)
        {
            if (el[i].ValueKind != JsonValueKind.Number) continue;
            t.Add(tl[i].GetInt64() * 1000);
            v.Add(el[i].GetDouble());
        }
        return new([.. t], [.. v]);
    }

    public async Task<List<Bar>> GetBars(string symbol, string timeframe, int limit, CancellationToken ct = default)
    {
        var start = DateTimeOffset.UtcNow.AddDays(timeframe == "1Day" ? -400 : -10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);
        var q = $"timeframe={Uri.EscapeDataString(timeframe)}&start={Uri.EscapeDataString(start)}&limit={limit}&sort=desc";
        var crypto = symbol.Contains('/');
        var path = crypto
            ? $"/v1beta3/crypto/us/bars?symbols={Uri.EscapeDataString(symbol)}&{q}"
            : $"/v2/stocks/{Uri.EscapeDataString(symbol)}/bars?{q}&feed=iex&adjustment=raw";
        using var d = await Send(HttpMethod.Get, path, null, ct, data: true);
        if (d is null || !d.RootElement.TryGetProperty("bars", out var bars)) return [];
        if (crypto) bars = bars.TryGetProperty(symbol, out var arr) ? arr : default;
        if (bars.ValueKind != JsonValueKind.Array) return [];
        var list = bars.EnumerateArray().Select(b => new Bar(Time(b, "t").ToUnixTimeMilliseconds(), Dec(b, "o"), Dec(b, "h"), Dec(b, "l"), Dec(b, "c"), Dec(b, "v"))).ToList();
        list.Reverse(); // asked newest first so the limit keeps the latest bars
        return list;
    }
}

public static class GatewayExtensions
{
    public static async Task<int> PingMs(this IBrokerGateway g, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await g.GetAccount(ct);
        return (int)sw.ElapsedMilliseconds;
    }
}
