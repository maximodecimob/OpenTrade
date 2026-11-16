using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTrade.Api;

namespace OpenTrade.Tests;

/// <summary>An in-memory stand-in for Alpaca.</summary>
public sealed class FakeBroker : IBrokerGateway
{
    public const string GoodSecret = "good-secret-0123456789";
    public const string GoodKeyId = "PKTEST0123456789";
    public readonly List<RemoteOrder> Orders = [];
    public List<PositionInfo> Positions = [new("AAPL", 10, 200m, 220m, 2200m, 200m, 0.1m, "us_equity"), new("BTC/USD", 1, 60000m, 61000m, 61000m, 1000m, 0.016m, "crypto")];
    public AccountInfo Account = new("PA123", "ACTIVE", 100_000m, 99_000m, 40_000m, 80_000m);
    public int Placed;

    public Task<AccountInfo> GetAccount(CancellationToken ct = default) => Task.FromResult(Account);
    public Task<List<PositionInfo>> GetPositions(CancellationToken ct = default) => Task.FromResult(Positions.ToList());
    public Task<List<RemoteOrder>> GetOrders(CancellationToken ct = default) => Task.FromResult(Orders.OrderByDescending(o => o.CreatedAt).ToList());

    public Task<RemoteOrder> PlaceOrder(NewOrder o, CancellationToken ct = default)
    {
        if (o.Symbol == "REJECT") throw new BrokerException("insufficient buying power", 422);
        Placed++;
        var r = new RemoteOrder(Guid.NewGuid().ToString(), o.ClientOrderId, o.Symbol, o.Side, o.Type, o.Qty, 0, o.LimitPrice, null, OrderStatus.Pending, o.Tif, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Orders.Add(r);
        return Task.FromResult(r);
    }

    public void Fill(string id, decimal price)
    {
        var i = Orders.FindIndex(o => o.Id == id);
        Orders[i] = Orders[i] with { Filled = Orders[i].Qty, AvgFill = price, Status = OrderStatus.Filled, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public void AddExternal(string symbol, decimal qty)
    {
        var now = DateTimeOffset.UtcNow;
        Orders.Add(new(Guid.NewGuid().ToString(), "from-alpaca-web", symbol, Side.Buy, OrderType.Market, qty, qty, null, 10m, OrderStatus.Filled, "day", now, now));
    }

    public Task CancelOrder(string id, CancellationToken ct = default)
    {
        var i = Orders.FindIndex(o => o.Id == id);
        if (i < 0 || Orders[i].Status is OrderStatus.Filled) throw new BrokerException("order is not cancelable", 422);
        Orders[i] = Orders[i] with { Status = OrderStatus.Cancelled };
        return Task.CompletedTask;
    }

    public Task<int> CancelAllOrders(CancellationToken ct = default)
    {
        var n = 0;
        for (var i = 0; i < Orders.Count; i++)
            if (Orders[i].Status is OrderStatus.Pending or OrderStatus.Partial) { Orders[i] = Orders[i] with { Status = OrderStatus.Cancelled }; n++; }
        return Task.FromResult(n);
    }

    public Task ClosePosition(string symbol, CancellationToken ct = default) { Positions = Positions.Where(p => p.Symbol != symbol).ToList(); return Task.CompletedTask; }
    public Task<List<AssetInfo>> GetAssets(CancellationToken ct = default) => Task.FromResult(new List<AssetInfo>
    {
        new("AAPL", "Apple Inc.", "NASDAQ", "us_equity", true, true, true), new("MSFT", "Microsoft Corporation", "NASDAQ", "us_equity", true, true, true),
        new("OLD", "Delisted Corp", "NYSE", "us_equity", false, false, false), new("BTC/USD", "Bitcoin", "CRYPTO", "crypto", true, false, true),
    });
    public Task<List<Bar>> GetBars(string symbol, string timeframe, int limit, CancellationToken ct = default) =>
        Task.FromResult(Enumerable.Range(0, Math.Min(limit, 1000)).Select(i => new Bar(1_000 + i, 10 + i, 11 + i, 9 + i, BarCloseOf(symbol, i, limit), 100)).ToList());

    /// <summary>Per-symbol price shape: UP rises, DOWN falls, anything else rises from 10.5.</summary>
    public static decimal BarCloseOf(string symbol, int i, int n) => symbol.StartsWith("DOWN") ? 100m - i : 10.5m + i;
    public Task<History> GetHistory(string period, string timeframe, CancellationToken ct = default) =>
        Task.FromResult(new History([1_000, 2_000, 3_000], [98_000d, 99_000d, 100_000d]));
}

public sealed class FakeFactory(FakeBroker fake) : IBrokerFactory
{
    public IBrokerGateway Create(Broker broker, string secret) =>
        secret == FakeBroker.GoodSecret ? fake : throw new BrokerException("Alpaca rejected the API credentials", 401);
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Email = "admin@admin.com", Password = "correct-horse-battery";
    public readonly FakeBroker Fake = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "opentrade-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment(Environments.Development);
        // UseSetting is applied before Program.cs reads configuration (ConfigureAppConfiguration is too late for minimal hosting).
        b.UseSetting("Admin:Email", Email);
        b.UseSetting("Admin:Password", Password);
        b.UseSetting("Data:Dir", _dir);
        b.UseSetting("Sync:Enabled", "false");
        b.UseSetting("RateLimit:LoginPerMinute", "1000");
        b.ConfigureServices(s =>
        {
            s.RemoveAll<IBrokerFactory>();
            s.AddSingleton<IBrokerFactory>(new FakeFactory(Fake));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch { /* sqlite may still hold the file */ }
    }
}

static class ServiceExt
{
    public static void RemoveAll<T>(this IServiceCollection s)
    {
        foreach (var d in s.Where(x => x.ServiceType == typeof(T)).ToList()) s.Remove(d);
    }
}

/// <summary>Small HTTP helper that sends the CSRF header and decodes JSON.</summary>
public sealed class Session(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public async Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Add("X-OT", "1");
        if (body is not null) req.Content = JsonContent.Create(body);
        return await Http.SendAsync(req);
    }
    public Task<HttpResponseMessage> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body);
    public Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, body);
    public Task<HttpResponseMessage> Delete(string url) => Send(HttpMethod.Delete, url);
    public Task<JsonElement> Get(string url) => Http.GetFromJsonAsync<JsonElement>(url);

    public static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public static Session Anonymous(ApiFactory f) => new(f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));

    public static async Task<Session> Login(ApiFactory f, string email = ApiFactory.Email, string password = ApiFactory.Password)
    {
        var s = Anonymous(f);
        var r = await s.Post("/api/login", new { email, password });
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException("login failed: " + await r.Content.ReadAsStringAsync());
        return s;
    }

    /// <summary>A client that uses a bearer API key and no cookies.</summary>
    public static Session Bearer(ApiFactory f, string key)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        c.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);
        return new Session(c);
    }
}
