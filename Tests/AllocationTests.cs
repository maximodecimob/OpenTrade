using System.Net;
using System.Text.Json;
using Xunit;

namespace OpenTrade.Tests;

/// <summary>Bot capital: percentages of the account that never exceed 100%, and orders that cannot spend more than the allocation.</summary>
public class AllocationTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory _f;
    public AllocationTests(ApiFactory f) => _f = f;

    static string Unique(string p) => p + Guid.NewGuid().ToString("N")[..6];

    async Task<(Session Admin, string BrokerId)> AdminWithBroker()
    {
        var admin = await Session.Login(_f);
        var list = await admin.Get("/api/brokers");
        if (list.GetArrayLength() > 0) return (admin, list[0].GetProperty("id").GetString()!);
        var r = await admin.Post("/api/brokers", new { name = "Alpaca Paper", keyId = FakeBroker.GoodKeyId, secret = FakeBroker.GoodSecret, paper = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (admin, (await Session.Json(r)).GetProperty("id").GetString()!);
    }

    static async Task<string> NewBot(Session admin, string brokerId, decimal pct)
    {
        var r = await admin.Post("/api/bots", new { name = Unique("bot"), brokerId, allocationPct = pct });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Session.Json(r)).GetProperty("id").GetString()!;
    }

    static async Task<string> NewKey(Session admin, string brokerId, string? botId)
    {
        var r = await admin.Post("/api/keys", new { name = Unique("key"), brokerId, botId, perms = new[] { "read", "positions", "trade" }, expiresInDays = 30 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Session.Json(r)).GetProperty("secret").GetString()!;
    }

    static async Task<string> ErrorOf(HttpResponseMessage r) => (await Session.Json(r)).GetProperty("error").GetString()!;

    [Fact]
    public async Task Allocations_are_percentages_and_never_exceed_100()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var a = await NewBot(admin, brokerId, 60);
        var b = await NewBot(admin, brokerId, 40);
        try
        {
            // the account is fully allocated: one more percent is refused, on create and on edit
            var over = await admin.Post("/api/bots", new { name = "too much", brokerId, allocationPct = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
            Assert.Contains("100%", await ErrorOf(over));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Put("/api/bots/" + b, new { name = "b", allocationPct = 41 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/bots", new { name = "x", brokerId, allocationPct = 101 })).StatusCode);

            // 100% of a 100,000 account is 100,000 USD, shared 60/40
            var bots = await admin.Get("/api/bots");
            Assert.Equal(100m, bots.GetProperty("summary").GetProperty("allocPct").GetDecimal());
            Assert.Equal(100_000m, bots.GetProperty("summary").GetProperty("alloc").GetDecimal());
            Assert.Equal(60_000m, (await admin.Get("/api/bots/" + a)).GetProperty("bot").GetProperty("allocated").GetDecimal());

            // the slider endpoint moves several bots at once and checks the total as a whole
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Put("/api/bots/allocations", new { items = new[] { new { id = a, pct = 70m }, new { id = b, pct = 40m } } })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.Put("/api/bots/allocations", new { items = new[] { new { id = a, pct = 30m }, new { id = b, pct = 70m } } })).StatusCode);
            Assert.Equal(30m, (await admin.Get("/api/bots/" + a)).GetProperty("bot").GetProperty("allocationPct").GetDecimal());
            Assert.Equal(70_000m, (await admin.Get("/api/bots/" + b)).GetProperty("bot").GetProperty("allocated").GetDecimal());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Put("/api/bots/allocations", new { items = new[] { new { id = a, pct = -1m } } })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.Put("/api/bots/allocations", new { items = new[] { new { id = "nope", pct = 1m } } })).StatusCode);
        }
        finally { await admin.Delete("/api/bots/" + a); await admin.Delete("/api/bots/" + b); }
    }

    [Fact]
    public async Task A_bot_cannot_spend_more_than_its_allocation()
    {
        var (admin, brokerId) = await AdminWithBroker();
        _f.Fake.Last["SPEND"] = 10m;
        var funded = await NewBot(admin, brokerId, 1);      // 1% of 100,000 = 1,000 USD
        var empty = await NewBot(admin, brokerId, 0);
        try
        {
            var bot = Session.Bearer(_f, await NewKey(admin, brokerId, funded));
            Assert.Equal(HttpStatusCode.Created, (await bot.Post("/v1/orders", new { symbol = "SPEND", side = "buy", type = "market", qty = 50 })).StatusCode);   // 500 USD
            var placed = _f.Fake.Placed;
            var refused = await bot.Post("/v1/orders", new { symbol = "SPEND", side = "buy", type = "market", qty = 60 });                                         // 500 open + 600 > 1,000
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("allocation", await ErrorOf(refused));
            Assert.Equal(placed, _f.Fake.Placed);                                                                                                                  // nothing reached the broker
            Assert.Equal(HttpStatusCode.Created, (await bot.Post("/v1/orders", new { symbol = "SPEND", side = "buy", type = "limit", qty = 50, limitPrice = 10 })).StatusCode); // exactly 1,000

            // a bot without capital cannot buy at all, from the API or from the dashboard
            var none = Session.Bearer(_f, await NewKey(admin, brokerId, empty));
            Assert.Equal(HttpStatusCode.Conflict, (await none.Post("/v1/orders", new { symbol = "SPEND", side = "buy", type = "market", qty = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.Post("/api/orders", new { brokerId, botId = empty, symbol = "SPEND", side = "buy", type = "market", qty = 1 })).StatusCode);
        }
        finally { await admin.Delete("/api/bots/" + funded); await admin.Delete("/api/bots/" + empty); }
    }

    [Fact]
    public async Task A_bot_cannot_sell_what_it_does_not_hold()
    {
        var (admin, brokerId) = await AdminWithBroker();
        _f.Fake.Last["SELLME"] = 10m;
        var id = await NewBot(admin, brokerId, 1);
        try
        {
            var bot = Session.Bearer(_f, await NewKey(admin, brokerId, id));
            Assert.Equal(HttpStatusCode.Conflict, (await bot.Post("/v1/orders", new { symbol = "SELLME", side = "sell", type = "market", qty = 1 })).StatusCode);

            var buy = await Session.Json(await bot.Post("/v1/orders", new { symbol = "SELLME", side = "buy", type = "market", qty = 5 }));
            _f.Fake.Fill(_f.Fake.Orders.First(o => o.ClientOrderId == buy.GetProperty("id").GetString()).Id, 10m);
            await admin.Post($"/api/brokers/{brokerId}/sync");

            Assert.Equal(HttpStatusCode.Conflict, (await bot.Post("/v1/orders", new { symbol = "SELLME", side = "sell", type = "market", qty = 6 })).StatusCode);   // holds 5
            Assert.Equal(HttpStatusCode.Created, (await bot.Post("/v1/orders", new { symbol = "SELLME", side = "sell", type = "market", qty = 5 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await bot.Post("/v1/orders", new { symbol = "SELLME", side = "sell", type = "market", qty = 1 })).StatusCode);   // the 5 are already being sold
        }
        finally { await admin.Delete("/api/bots/" + id); }
    }

    [Fact]
    public async Task A_client_order_id_makes_retries_safe()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var key = Session.Bearer(_f, await NewKey(admin, brokerId, null));
        var body = new { clientOrderId = "retry-" + Guid.NewGuid().ToString("N")[..8], symbol = "IDEM", side = "buy", type = "limit", qty = 1, limitPrice = 5 };

        var first = await key.Post("/v1/orders", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var placed = _f.Fake.Placed;

        var again = await key.Post("/v1/orders", body);                  // e.g. the bot timed out and retried
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await Session.Json(first)).GetProperty("id").GetString(), (await Session.Json(again)).GetProperty("id").GetString());
        Assert.Equal(placed, _f.Fake.Placed);                            // no second order at the broker

        Assert.Equal(HttpStatusCode.BadRequest, (await key.Post("/v1/orders", new { clientOrderId = "x", symbol = "IDEM", side = "buy", type = "market", qty = 1 })).StatusCode);
    }
}
