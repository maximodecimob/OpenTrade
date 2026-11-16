using System.Net;
using System.Text;
using OpenTrade.Api;
using Xunit;

namespace OpenTrade.Tests;

public class LedgerTests
{
    static Order O(string sym, Side side, decimal qty, decimal px, long t) =>
        new() { Symbol = sym, Side = side, Qty = qty, Filled = qty, AvgFill = px, Status = OrderStatus.Filled, CreatedMs = t };

    [Fact]
    public void Average_cost_realized_and_unrealized()
    {
        var st = Ledger.Compute([O("AAPL", Side.Buy, 10, 100, 1), O("AAPL", Side.Buy, 10, 120, 2), O("AAPL", Side.Sell, 5, 130, 3)],
            new Dictionary<string, decimal> { ["AAPL"] = 140m });
        var p = Assert.Single(st.Positions);
        Assert.Equal(15, p.Qty);
        Assert.Equal(110m, p.Avg);
        Assert.Equal(100m, st.Realized);          // (130-110)*5
        Assert.Equal(450m, st.Unrealized);        // (140-110)*15
        Assert.Equal(550m, st.Pnl);
        Assert.Equal(2100m, st.Invested);
    }

    [Fact]
    public void Closed_position_disappears_and_unfilled_orders_are_ignored()
    {
        var pending = new Order { Symbol = "MSFT", Side = Side.Buy, Qty = 5, Filled = 0, LimitPrice = 1m, Status = OrderStatus.Pending };
        var st = Ledger.Compute([O("TSLA", Side.Buy, 2, 10, 1), O("TSLA", Side.Sell, 2, 15, 2), pending], new Dictionary<string, decimal>());
        Assert.Empty(st.Positions);
        Assert.Equal(10m, st.Realized);
    }

    [Fact]
    public void Missing_price_falls_back_to_average_cost() =>
        Assert.Equal(0m, Ledger.Compute([O("X", Side.Buy, 3, 9, 1)], new Dictionary<string, decimal>()).Unrealized);

    [Fact]
    public void Selling_more_than_held_does_not_go_negative()
    {
        var st = Ledger.Compute([O("X", Side.Buy, 1, 10, 1), O("X", Side.Sell, 5, 12, 2)], new Dictionary<string, decimal>());
        Assert.Empty(st.Positions);
        Assert.Equal(2m, st.Realized);
    }
}

public class ValidationTests
{
    static (NewOrder? o, string? e) V(string? sym = "aapl", string? side = "buy", string? type = "market", decimal? qty = 1, decimal? lp = null, string? tif = null) =>
        OrderService.Validate(new OrderRequest(sym, side, type, qty, lp, tif), "OT-1");

    [Fact]
    public void Valid_market_order_is_normalized()
    {
        var (o, e) = V();
        Assert.Null(e);
        Assert.Equal("AAPL", o!.Symbol);
        Assert.Equal("day", o.Tif);
        Assert.Equal(OrderType.Market, o.Type);
    }

    [Theory]
    [InlineData("", "buy", "market", 1, null)]
    [InlineData("AAPL;DROP", "buy", "market", 1, null)]
    [InlineData("AAPL", "hold", "market", 1, null)]
    [InlineData("AAPL", "buy", "stop", 1, null)]
    [InlineData("AAPL", "buy", "market", 0, null)]
    [InlineData("AAPL", "buy", "market", -3, null)]
    [InlineData("AAPL", "buy", "limit", 1, null)]
    [InlineData("AAPL", "buy", "limit", 1, 0)]
    public void Invalid_orders_are_rejected(string sym, string side, string type, int qty, int? lp) =>
        Assert.NotNull(V(sym, side, type, qty, lp).e);

    [Fact]
    public void Bad_time_in_force_is_rejected() => Assert.NotNull(V(tif: "ioc").e);

    [Fact]
    public void Limit_order_keeps_price_and_crypto_symbols_are_allowed()
    {
        var (o, e) = V("btc/usd", "sell", "limit", 0.5m, 61_000.25m, "gtc");
        Assert.Null(e);
        Assert.Equal(61_000.25m, o!.LimitPrice);
        Assert.Equal("BTC/USD", o.Symbol);
    }
}

public class PasswordTests
{
    [Fact]
    public void Hash_verifies_only_the_right_password()
    {
        var (salt, hash) = Passwords.Create("a-long-enough-password");
        Assert.True(Passwords.Verify("a-long-enough-password", salt, hash));
        Assert.False(Passwords.Verify("a-long-enough-passwore", salt, hash));
    }

    [Fact]
    public void Salts_differ_between_hashes() => Assert.NotEqual(Passwords.Create("same-password-123").Salt, Passwords.Create("same-password-123").Salt);

    [Theory]
    [InlineData("short", true)]
    [InlineData("exactly12chr", false)]
    public void Minimum_length(string pw, bool bad) => Assert.Equal(bad, Passwords.Validate(pw) is not null);
}

/// <summary>Checks the Alpaca JSON mapping against canned responses, since real calls need real keys.</summary>
public class AlpacaGatewayTests
{
    sealed class Canned(Func<HttpRequestMessage, (HttpStatusCode, string)> f) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = [];
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add(r);
            Bodies.Add(r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct));
            var (code, body) = f(r);
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    static AlpacaGateway G(Canned h) => new(new HttpClient(h), "https://paper.example", "KEYID", "SECRET");

    [Fact]
    public async Task Account_and_headers()
    {
        var h = new Canned(_ => (HttpStatusCode.OK, """{"account_number":"PA1","status":"ACTIVE","equity":"100500.25","last_equity":"100000","cash":"25000.5","buying_power":"200000"}"""));
        var a = await G(h).GetAccount();
        Assert.Equal(100500.25m, a.Equity);
        Assert.Equal(25000.5m, a.Cash);
        Assert.Equal("https://paper.example/v2/account", h.Seen[0].RequestUri!.ToString());
        Assert.Equal("KEYID", h.Seen[0].Headers.GetValues("APCA-API-KEY-ID").Single());
        Assert.Equal("SECRET", h.Seen[0].Headers.GetValues("APCA-API-SECRET-KEY").Single());
    }

    [Fact]
    public async Task Positions_are_parsed()
    {
        var h = new Canned(_ => (HttpStatusCode.OK, """[{"symbol":"AAPL","qty":"10","avg_entry_price":"200","current_price":"210.5","market_value":"2105","unrealized_pl":"105","unrealized_plpc":"0.0525","asset_class":"us_equity"}]"""));
        var p = Assert.Single(await G(h).GetPositions());
        Assert.Equal(("AAPL", 10m, 210.5m, 105m), (p.Symbol, p.Qty, p.Last, p.UnrealizedPl));
    }

    [Theory]
    [InlineData("new", OrderStatus.Pending)]
    [InlineData("accepted", OrderStatus.Pending)]
    [InlineData("partially_filled", OrderStatus.Partial)]
    [InlineData("filled", OrderStatus.Filled)]
    [InlineData("canceled", OrderStatus.Cancelled)]
    [InlineData("expired", OrderStatus.Cancelled)]
    [InlineData("rejected", OrderStatus.Rejected)]
    public void Status_mapping(string alpaca, OrderStatus ours) => Assert.Equal(ours, AlpacaGateway.MapStatus(alpaca));

    [Fact]
    public async Task Place_order_sends_expected_body_and_parses_answer()
    {
        var h = new Canned(_ => (HttpStatusCode.OK, """{"id":"abc","client_order_id":"OT-1","symbol":"AAPL","side":"buy","type":"limit","qty":"2","filled_qty":"0","limit_price":"150.5","status":"accepted","time_in_force":"day","created_at":"2026-11-12T10:00:00Z","updated_at":"2026-11-12T10:00:00Z"}"""));
        var r = await G(h).PlaceOrder(new NewOrder("AAPL", Side.Buy, OrderType.Limit, 2, 150.5m, "day", "OT-1"));
        Assert.Equal(("abc", OrderType.Limit, 150.5m, OrderStatus.Pending), (r.Id, r.Type, r.LimitPrice, r.Status));
        Assert.Contains("\"limit_price\":\"150.5\"", h.Bodies[0]);
        Assert.Contains("\"client_order_id\":\"OT-1\"", h.Bodies[0]);
        Assert.Contains("\"side\":\"buy\"", h.Bodies[0]);
    }

    [Fact]
    public async Task Rejected_credentials_and_api_messages_become_BrokerExceptions()
    {
        var bad = await Assert.ThrowsAsync<BrokerException>(() => G(new Canned(_ => (HttpStatusCode.Forbidden, """{"message":"forbidden."}"""))).GetAccount());
        Assert.Contains("credentials", bad.Message);
        var low = await Assert.ThrowsAsync<BrokerException>(() => G(new Canned(_ => (HttpStatusCode.UnprocessableEntity, """{"message":"insufficient buying power"}"""))).GetAccount());
        Assert.Equal("insufficient buying power", low.Message);
        Assert.Equal(422, low.Status);
    }

    [Fact]
    public async Task History_skips_null_points()
    {
        var h = new Canned(_ => (HttpStatusCode.OK, """{"timestamp":[10,20,30],"equity":[100.5,null,102],"base_value":100}"""));
        var hist = await G(h).GetHistory("1M", "1D");
        Assert.Equal([10_000L, 30_000L], hist.T);
        Assert.Equal([100.5, 102d], hist.V);
    }

    [Fact]
    public async Task Bars_for_stocks_and_crypto_are_returned_oldest_first()
    {
        var h = new Canned(_ => (HttpStatusCode.OK, """{"bars":[{"t":"2026-11-12T10:00:00Z","o":3,"h":4,"l":2,"c":3.5,"v":10},{"t":"2026-11-11T10:00:00Z","o":1,"h":2,"l":0.5,"c":1.5,"v":9}]}"""));
        var bars = await G(h).GetBars("AAPL", "1Day", 2);
        Assert.Equal([1.5m, 3.5m], bars.Select(b => b.Close));
        Assert.Contains("/v2/stocks/AAPL/bars", h.Seen[0].RequestUri!.ToString());
        Assert.StartsWith("https://data.alpaca.markets", h.Seen[0].RequestUri!.ToString());
        Assert.Contains("sort=desc", h.Seen[0].RequestUri!.Query);

        var hc = new Canned(_ => (HttpStatusCode.OK, """{"bars":{"BTC/USD":[{"t":"2026-11-12T10:00:00Z","o":1,"h":1,"l":1,"c":61000,"v":1}]}}"""));
        var c = Assert.Single(await G(hc).GetBars("BTC/USD", "1Hour", 5));
        Assert.Equal(61000m, c.Close);
        Assert.Contains("/v1beta3/crypto/us/bars", hc.Seen[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Cancel_all_counts_results()
    {
        var h = new Canned(_ => (HttpStatusCode.MultiStatus, """[{"id":"1","status":200},{"id":"2","status":200}]"""));
        Assert.Equal(2, await G(h).CancelAllOrders());
    }
}
