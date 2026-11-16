using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenTrade.Bots;
using Xunit;

namespace OpenTrade.Tests;

public class StrategyTests
{
    static decimal[] Up(int n = 40) => Enumerable.Range(10, n).Select(x => (decimal)x).ToArray();

    [Fact]
    public void Sma_needs_enough_data()
    {
        Assert.Equal(3.5m, Strategies.Sma([1, 2, 3, 4], 2));
        Assert.Null(Strategies.Sma([1], 2));
    }

    [Fact]
    public void Crossover_follows_the_trend()
    {
        Assert.Equal(Strategies.Trend.Long, Strategies.Crossover(Up()));
        Assert.Equal(Strategies.Trend.Flat, Strategies.Crossover(Up().Reverse().ToArray()));
        Assert.Null(Strategies.Crossover([1, 2, 3]));
    }

    [Fact]
    public void Rsi_extremes_and_known_value()
    {
        Assert.Equal(100, Strategies.Rsi(Up())!.Value);
        Assert.True(Strategies.Rsi(Up().Reverse().ToArray()) < 1);
        Assert.Null(Strategies.Rsi([1, 2, 3]));
        // Wilder's worked example: the first 14-period RSI is about 70.5
        decimal[] closes = [44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m, 45.84m, 46.08m, 45.89m, 46.03m, 45.61m, 46.28m, 46.28m];
        Assert.InRange(Strategies.Rsi(closes)!.Value, 70.4, 70.7);
    }

    [Theory]
    [InlineData(20, false, Strategies.Action.Buy)]
    [InlineData(20, true, Strategies.Action.Hold)]
    [InlineData(60, true, Strategies.Action.Sell)]
    [InlineData(60, false, Strategies.Action.Hold)]
    [InlineData(45, true, Strategies.Action.Hold)]
    public void Rsi_action(double rsi, bool held, Strategies.Action expected) => Assert.Equal(expected, Strategies.RsiAction(rsi, held));

    [Fact]
    public void Rsi_action_without_data_holds() => Assert.Equal(Strategies.Action.Hold, Strategies.RsiAction(null, false));

    [Fact]
    public void Shares_for_budget()
    {
        Assert.Equal(3, Strategies.SharesFor(1000, 300));
        Assert.Equal(0, Strategies.SharesFor(100, 300));
        Assert.Equal(0.167m, Strategies.SharesFor(50, 300, fractional: true));
        Assert.Equal(0, Strategies.SharesFor(50, 0));
    }
}

public class BotStepTests
{
    sealed class FakeClient(Dictionary<string, decimal[]> closes, Dictionary<string, decimal>? held = null, params string[] open) : IOpenTradeClient
    {
        public readonly List<OrderTicket> Placed = [];
        public Task<BotSelf> GetBot(CancellationToken ct = default) => Task.FromResult(new BotSelf("b", "bot", true, 10_000));
        public Task<Dictionary<string, Holding>> GetPositions(CancellationToken ct = default) =>
            Task.FromResult((held ?? []).ToDictionary(kv => kv.Key, kv => new Holding(kv.Key, kv.Value, kv.Value * 10)));
        public Task<List<PlacedOrder>> GetOpenOrders(CancellationToken ct = default) => Task.FromResult(open.Select(s => new PlacedOrder("o", s, "pending")).ToList());
        public Task<List<Candle>> GetBars(string symbol, string timeframe, int limit, CancellationToken ct = default) =>
            Task.FromResult(closes[symbol].TakeLast(limit).Select(c => new Candle(DateTimeOffset.UtcNow, c, c, c, c, 1)).ToList());
        public Task<PlacedOrder> PlaceOrder(OrderTicket t, CancellationToken ct = default) { Placed.Add(t); return Task.FromResult(new PlacedOrder("OT-1", t.Symbol, "pending")); }
    }

    static readonly BotSelf Self = new("b", "bot", true, 10_000);
    static decimal[] Up => Enumerable.Range(10, 40).Select(x => (decimal)x).ToArray();
    static decimal[] Down => Enumerable.Range(10, 40).Reverse().Select(x => (decimal)x).ToArray();
    static void Quiet(string _) { }

    [Fact]
    public async Task Sma_buys_an_uptrend_with_the_budget_split_between_symbols()
    {
        var c = new FakeClient(new() { ["AAA"] = Up, ["BBB"] = Up });
        await new SmaCrossoverBot().Step(c, Self, new BotOptions(["AAA", "BBB"]), Quiet, default);
        Assert.Equal(["AAA", "BBB"], c.Placed.Select(t => t.Symbol));
        Assert.All(c.Placed, t => Assert.Equal("buy", t.Side));
        Assert.All(c.Placed, t => Assert.Equal(Math.Floor(5000m / 49), t.Qty));
    }

    [Fact]
    public async Task Sma_never_buys_twice_and_skips_symbols_with_open_orders()
    {
        var held = new FakeClient(new() { ["AAA"] = Up }, new() { ["AAA"] = 3 });
        await new SmaCrossoverBot().Step(held, Self, new BotOptions(["AAA"]), Quiet, default);
        Assert.Empty(held.Placed);
        var busy = new FakeClient(new() { ["AAA"] = Up }, null, "AAA");
        await new SmaCrossoverBot().Step(busy, Self, new BotOptions(["AAA"]), Quiet, default);
        Assert.Empty(busy.Placed);
    }

    [Fact]
    public async Task Sma_sells_everything_on_a_downtrend()
    {
        var c = new FakeClient(new() { ["AAA"] = Down }, new() { ["AAA"] = 12 });
        await new SmaCrossoverBot().Step(c, Self, new BotOptions(["AAA"]), Quiet, default);
        var t = Assert.Single(c.Placed);
        Assert.Equal(("AAA", "sell", 12m), (t.Symbol, t.Side, t.Qty));
    }

    [Fact]
    public async Task Dry_run_places_nothing_but_says_what_it_would_do()
    {
        var c = new FakeClient(new() { ["AAA"] = Up });
        var log = new List<string>();
        await new SmaCrossoverBot().Step(c, Self, new BotOptions(["AAA"], DryRun: true), log.Add, default);
        Assert.Empty(c.Placed);
        Assert.Contains(log, l => l.Contains("dry run") && l.Contains("BUY"));
    }

    [Fact]
    public async Task Rsi_buys_oversold_and_sells_recovered()
    {
        var c = new FakeClient(new() { ["AAA"] = Down });
        await new RsiMeanReversionBot().Step(c, Self, new BotOptions(["AAA"]), Quiet, default);
        Assert.Equal("buy", Assert.Single(c.Placed).Side);
        var c2 = new FakeClient(new() { ["AAA"] = Up }, new() { ["AAA"] = 7 });
        await new RsiMeanReversionBot().Step(c2, Self, new BotOptions(["AAA"]), Quiet, default);
        var t = Assert.Single(c2.Placed);
        Assert.Equal(("sell", 7m), (t.Side, t.Qty));
    }

    [Fact]
    public async Task Dca_buys_fractional_shares_then_waits_for_the_next_period()
    {
        var state = Path.Combine(Path.GetTempPath(), "dca-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var c = new FakeClient(new() { ["SPY"] = [200m] });
            var bot = new DcaBot(state);
            await bot.Step(c, Self, new BotOptions(["SPY"], Amount: 50), Quiet, default);
            var t = Assert.Single(c.Placed);
            Assert.Equal(0.25m, t.Qty);
            await bot.Step(c, Self, new BotOptions(["SPY"], Amount: 50), Quiet, default);   // same day: nothing new
            Assert.Single(c.Placed);
        }
        finally { File.Delete(state); }
    }

    [Fact]
    public async Task Dca_stops_at_the_allocation()
    {
        var state = Path.Combine(Path.GetTempPath(), "dca-" + Guid.NewGuid().ToString("N") + ".json");
        var c = new FakeClient(new() { ["SPY"] = [100m] }, new() { ["SPY"] = 999 });   // held value 9,990 of 10,000
        await new DcaBot(state).Step(c, Self, new BotOptions(["SPY"], Amount: 50), Quiet, default);
        Assert.Empty(c.Placed);
    }
}

/// <summary>Runs the real bots against the real API (with a fake broker behind it).</summary>
public class BotEndToEndTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory _f;
    public BotEndToEndTests(ApiFactory f) => _f = f;

    static IBot[] Bots() => [new SmaCrossoverBot(), new RsiMeanReversionBot(), new DcaBot(Path.Combine(Path.GetTempPath(), "dca-" + Guid.NewGuid().ToString("N") + ".json"))];

    [Fact]
    public async Task Setup_creates_bots_and_keys_then_the_bots_trade_through_the_api()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), "keys-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var admin = _f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            var s = new Session(admin);

            // no broker yet -> setup explains what to do
            var err = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Setup.Run(admin, ApiFactory.Email, ApiFactory.Password, Bots(), 9_000, null, keysPath, _ => { }));
            Assert.Contains("Connect broker", err.Message);

            Assert.Equal(HttpStatusCode.OK, (await s.Post("/api/brokers", new { name = "Alpaca Paper", keyId = FakeBroker.GoodKeyId, secret = FakeBroker.GoodSecret, paper = true })).StatusCode);
            var bots = Bots();
            var results = await Setup.Run(admin, ApiFactory.Email, ApiFactory.Password, bots, 9_000, null, keysPath, _ => { });
            Assert.All(results, r => Assert.StartsWith("created", r.Status));
            var keys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(keysPath))!;
            Assert.Equal(3, keys.Count);
            Assert.All(keys.Values, k => Assert.StartsWith("ot_test_", k));
            // running setup again does not duplicate anything
            Assert.All(await Setup.Run(admin, ApiFactory.Email, ApiFactory.Password, bots, 9_000, null, keysPath, _ => { }), r => Assert.StartsWith("already exists", r.Status));
            Assert.Equal(3, (await s.Get("/api/bots")).GetProperty("bots").GetArrayLength());

            // the bots, using only their API keys
            IOpenTradeClient Client(string name) => new OpenTradeClient("http://localhost", KeyStore.Load(name, keysPath), _f.CreateClient());
            var log = new List<string>();

            // dry run: nothing is placed
            var sma = new SmaCrossoverBot();
            Assert.True(await BotRunner.Tick(sma, Client(sma.Name), new BotOptions(["AAPL", "MSFT", "NVDA"], DryRun: true), log.Add, default));
            Assert.Equal(0, _f.Fake.Placed);
            Assert.Contains(log, l => l.Contains("dry run"));

            // real run: 3000 USD per symbol at a close of 44.5 -> 67 shares each
            Assert.True(await BotRunner.Tick(sma, Client(sma.Name), new BotOptions(["AAPL", "MSFT", "NVDA"]), log.Add, default));
            Assert.Equal(3, _f.Fake.Placed);
            Assert.All(_f.Fake.Orders, o => Assert.Equal(67m, o.Qty));

            // running again while orders are open must not stack duplicates
            await BotRunner.Tick(sma, Client(sma.Name), new BotOptions(["AAPL", "MSFT", "NVDA"]), log.Add, default);
            Assert.Equal(3, _f.Fake.Placed);

            // RSI bot buys a falling stock, DCA bot buys fractional index shares
            var rsi = new RsiMeanReversionBot();
            await BotRunner.Tick(rsi, Client(rsi.Name), new BotOptions(["DOWNX"]), log.Add, default);
            Assert.Equal(4, _f.Fake.Placed);
            var dca = Bots()[2];
            await BotRunner.Tick(dca, Client("DCA Index Buyer"), new BotOptions(["SPY"], Amount: 50), log.Add, default);
            Assert.Equal(5, _f.Fake.Placed);
            Assert.Contains(_f.Fake.Orders, o => o.Symbol == "SPY" && o.Qty == Math.Round(50m / 10.5m, 3));

            // the UI attributes the orders to the right bots, as API orders, and the bots show a heartbeat
            var list = (await s.Get("/api/orders?size=100")).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(5, list.Count(o => o.GetProperty("source").GetString() == "api"));
            Assert.Equal(3, list.Count(o => o.GetProperty("strategy").GetString() == "SMA Crossover"));
            Assert.Single(list, o => o.GetProperty("strategy").GetString() == "RSI Mean Reversion");
            Assert.All((await s.Get("/api/bots")).GetProperty("bots").EnumerateArray(), b => Assert.True(b.GetProperty("online").GetBoolean()));

            // pausing a bot in the UI stops it trading
            var smaId = (await s.Get("/api/bots")).GetProperty("bots").EnumerateArray().First(b => b.GetProperty("name").GetString() == "SMA Crossover").GetProperty("id").GetString();
            await s.Post($"/api/bots/{smaId}/pause");
            log.Clear();
            await BotRunner.Tick(sma, Client(sma.Name), new BotOptions(["AAPL", "MSFT", "NVDA"]), log.Add, default);
            Assert.Contains(log, l => l.Contains("paused"));
            Assert.Equal(5, _f.Fake.Placed);

            // an invalid key makes the runner stop instead of looping forever
            var bad = new OpenTradeClient("http://localhost", "ot_test_wrong", _f.CreateClient());
            Assert.False(await BotRunner.Tick(sma, bad, new BotOptions(["AAPL"]), log.Add, default));
        }
        finally { File.Delete(keysPath); }
    }

    [Fact]
    public async Task Client_maps_paused_and_error_responses()
    {
        var admin = _f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var s = new Session(admin);
        await s.Post("/api/login", new { email = ApiFactory.Email, password = ApiFactory.Password });
        var brokers = await s.Get("/api/brokers");
        var brokerId = brokers.GetArrayLength() > 0 ? brokers[0].GetProperty("id").GetString()
            : (await Session.Json(await s.Post("/api/brokers", new { name = "Alpaca Paper", keyId = FakeBroker.GoodKeyId, secret = FakeBroker.GoodSecret, paper = true }))).GetProperty("id").GetString();
        var bot = (await Session.Json(await s.Post("/api/bots", new { name = "Paused one", brokerId, allocated = 100 }))).GetProperty("id").GetString();
        var key = (await Session.Json(await s.Post("/api/keys", new { name = "k", brokerId, botId = bot, perms = new[] { "read", "trade" }, expiresInDays = 30 }))).GetProperty("secret").GetString()!;
        var client = new OpenTradeClient("http://localhost", key, _f.CreateClient());
        Assert.True((await client.GetBot()).Running);
        await s.Post($"/api/bots/{bot}/pause");
        await Assert.ThrowsAsync<BotPausedException>(() => client.PlaceOrder(new OrderTicket("AAPL", "buy", 1)));
        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetPositions());      // the key has no 'positions' permission
        Assert.Equal(403, ex.Status);
    }
}
