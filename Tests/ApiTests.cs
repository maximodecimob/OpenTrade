using System.Net;
using System.Text.Json;
using Xunit;

namespace OpenTrade.Tests;

public class ApiTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory _f;
    public ApiTests(ApiFactory f) => _f = f;

    static string Unique(string p) => p + Guid.NewGuid().ToString("N")[..6];

    // ---------- helpers ----------
    async Task<(Session Admin, string BrokerId)> AdminWithBroker()
    {
        var admin = await Session.Login(_f);
        var list = await admin.Get("/api/brokers");
        if (list.GetArrayLength() > 0) return (admin, list[0].GetProperty("id").GetString()!);
        var r = await admin.Post("/api/brokers", new { name = "Alpaca Paper", keyId = FakeBroker.GoodKeyId, secret = FakeBroker.GoodSecret, paper = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (admin, (await Session.Json(r)).GetProperty("id").GetString()!);
    }

    async Task<string> CreateBot(Session admin, string brokerId, string? name = null, decimal pct = 1)
    {
        var r = await admin.Post("/api/bots", new { name = name ?? Unique("bot"), @class = "US equities", description = "test", brokerId, allocationPct = pct });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Session.Json(r)).GetProperty("id").GetString()!;
    }

    async Task<string> CreateKey(Session admin, string brokerId, string? botId, params string[] perms)
    {
        var r = await admin.Post("/api/keys", new { name = Unique("key"), brokerId, botId, perms, expiresInDays = 30 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Session.Json(r)).GetProperty("secret").GetString()!;
    }

    async Task<Session> LoginAs(Session admin, string role)
    {
        var email = Unique(role) + "@test.com";
        var r = await admin.Post("/api/users", new { email, name = role, role, password = "another-long-password" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Session.Login(_f, email, "another-long-password");
    }

    // ---------- auth ----------
    [Fact]
    public async Task Protected_endpoints_need_a_session()
    {
        foreach (var url in new[] { "/api/dashboard", "/api/orders", "/api/positions", "/api/assets", "/api/bots", "/api/brokers", "/api/keys", "/api/users" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Anonymous(_f).Http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Login_works_and_wrong_credentials_are_refused()
    {
        var anon = Session.Anonymous(_f);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/login", new { email = ApiFactory.Email, password = "wrong-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/login", new { email = "nobody@x.com", password = ApiFactory.Password })).StatusCode);
        var ok = await anon.Post("/api/login", new { email = "ADMIN@admin.com", password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var me = await anon.Get("/api/me");
        Assert.Equal("admin", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Writes_without_the_csrf_header_are_forbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Session.Login(_f)).Http.PostAsync("/api/orders/cancel-open", null)).StatusCode);

    [Fact]
    public async Task Security_headers_are_set()
    {
        var r = await Session.Anonymous(_f).Http.GetAsync("/api/me");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(r.Headers.Contains("Content-Security-Policy"));
    }

    // ---------- users & roles ----------
    [Fact]
    public async Task Change_password_requires_current_password()
    {
        var admin = await Session.Login(_f);
        var u = await LoginAs(admin, "trader");
        Assert.Equal(HttpStatusCode.BadRequest, (await u.Post("/api/me/password", new { current = "nope", password = "brand-new-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await u.Post("/api/me/password", new { current = "another-long-password", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await u.Post("/api/me/password", new { current = "another-long-password", password = "brand-new-password-1" })).StatusCode);
        var email = (await u.Get("/api/me")).GetProperty("email").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Anonymous(_f).Post("/api/login", new { email, password = "another-long-password" })).StatusCode);
        await Session.Login(_f, email, "brand-new-password-1");
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var viewer = await LoginAs(admin, "viewer");
        var trader = await LoginAs(admin, "trader");
        // viewer: can read, cannot trade or administer
        Assert.Equal(HttpStatusCode.OK, (await viewer.Http.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post("/api/orders", new { brokerId, symbol = "AAPL", side = "buy", type = "market", qty = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post("/api/orders/cancel-open")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.GetAsync("/api/keys")).StatusCode);
        // trader: can trade, cannot administer
        Assert.Equal(HttpStatusCode.OK, (await trader.Post("/api/orders", new { brokerId, symbol = "AAPL", side = "buy", type = "market", qty = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.Post("/api/bots", new { name = "x", brokerId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.Post("/api/users", new { email = "a@b.com", name = "x", role = "viewer", password = "long-enough-password" })).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_kicks_them_out_immediately()
    {
        var admin = await Session.Login(_f);
        var email = Unique("temp") + "@test.com";
        var created = await Session.Json(await admin.Post("/api/users", new { email, name = "Temp", role = "viewer", password = "another-long-password" }));
        var temp = await Session.Login(_f, email, "another-long-password");
        Assert.Equal(HttpStatusCode.OK, (await temp.Http.GetAsync("/api/orders")).StatusCode);
        await admin.Put("/api/users/" + created.GetProperty("id").GetString(), new { active = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await temp.Http.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Anonymous(_f).Post("/api/login", new { email, password = "another-long-password" })).StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_remove_themselves_or_the_last_admin()
    {
        var admin = await Session.Login(_f);
        var me = (await admin.Get("/api/me")).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Delete("/api/users/" + me)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Put("/api/users/" + me, new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Put("/api/users/" + me, new { role = "viewer" })).StatusCode);
    }

    [Fact]
    public async Task User_validation_and_duplicate_email()
    {
        var admin = await Session.Login(_f);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/users", new { email = "nope", name = "x", role = "viewer", password = "long-enough-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/users", new { email = "a@b.com", name = "x", role = "root", password = "long-enough-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/users", new { email = "a@b.com", name = "x", role = "viewer", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post("/api/users", new { email = ApiFactory.Email, name = "x", role = "viewer", password = "long-enough-password" })).StatusCode);
    }

    // ---------- brokers ----------
    [Fact]
    public async Task Broker_connection_validates_and_tests_credentials()
    {
        var admin = await Session.Login(_f);
        var bad = await admin.Post("/api/brokers", new { name = "Bad", keyId = "PKBAD0123456789", secret = "wrong-secret-0123456789", paper = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("credentials", (await Session.Json(bad)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/brokers", new { name = "x", keyId = "short", secret = "wrong-secret-0123456789", paper = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Post("/api/brokers", new { name = "Live", keyId = "PKLIVE0123456789", secret = FakeBroker.GoodSecret, paper = false })).StatusCode);

        var (_, id) = await AdminWithBroker();
        var list = await admin.Get("/api/brokers");
        var b = list.EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        Assert.True(b.GetProperty("connected").GetBoolean());
        Assert.Equal(100_000m, b.GetProperty("equity").GetDecimal());
        Assert.DoesNotContain(FakeBroker.GoodSecret, b.ToString());
        Assert.DoesNotContain(FakeBroker.GoodKeyId, b.ToString()); // only a masked key id is returned
        var t = await Session.Json(await admin.Post($"/api/brokers/{id}/test"));
        Assert.True(t.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Same_key_cannot_be_connected_twice()
    {
        var (admin, _) = await AdminWithBroker();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post("/api/brokers", new { name = "Again", keyId = FakeBroker.GoodKeyId, secret = FakeBroker.GoodSecret, paper = true })).StatusCode);
    }

    // ---------- dashboard, positions, assets ----------
    [Fact]
    public async Task Dashboard_positions_and_series_come_from_the_broker()
    {
        var (admin, _) = await AdminWithBroker();
        var d = await admin.Get("/api/dashboard");
        Assert.Equal(100_000m, d.GetProperty("nav").GetDecimal());
        Assert.Equal(1_000m, d.GetProperty("day").GetDecimal());       // equity - last_equity
        Assert.Equal(2, d.GetProperty("positions").GetInt32());
        Assert.Equal(2_000m, d.GetProperty("month").GetDecimal());     // 100000 - 98000 from the fake history
        var p = await admin.Get("/api/positions");
        Assert.Equal(2, p.GetProperty("totals").GetProperty("count").GetInt32());
        var s = await admin.Get("/api/series/portfolio?range=1M");
        Assert.Equal(3, s.GetProperty("t").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync("/api/series/portfolio?range=99Y")).StatusCode);
    }

    [Fact]
    public async Task Assets_are_filtered_and_paged()
    {
        var (admin, _) = await AdminWithBroker();
        var all = await admin.Get("/api/assets");
        Assert.Equal(3, all.GetProperty("total").GetInt32());   // the untradable one is hidden
        var msft = await admin.Get("/api/assets?q=micro");
        Assert.Equal("MSFT", msft.GetProperty("items")[0].GetProperty("symbol").GetString());
        var crypto = await admin.Get("/api/assets?cls=crypto");
        Assert.Equal(1, crypto.GetProperty("total").GetInt32());
    }

    // ---------- orders ----------
    [Fact]
    public async Task Quote_returns_the_latest_price_and_validates_the_symbol()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var q = await admin.Get($"/api/quote?symbol=aapl&brokerId={brokerId}");
        Assert.Equal("AAPL", q.GetProperty("symbol").GetString());
        Assert.Equal(10.5m, q.GetProperty("price").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Http.GetAsync("/api/quote?symbol=A%3BB")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Anonymous(_f).Http.GetAsync("/api/quote?symbol=AAPL")).StatusCode);
    }

    [Fact]
    public async Task Order_lifecycle_place_sync_fill_cancel()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var botId = await CreateBot(admin, brokerId);
        var placed = await admin.Post("/api/orders", new { brokerId, botId, symbol = "nvda", side = "buy", type = "limit", qty = 4, limitPrice = 100.5, timeInForce = "gtc" });
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        var o = await Session.Json(placed);
        var id = o.GetProperty("id").GetString()!;
        Assert.Equal("NVDA", o.GetProperty("symbol").GetString());
        Assert.Equal("pending", o.GetProperty("status").GetString());

        // the broker fills it; the next sync picks that up
        var remoteId = _f.Fake.Orders.First(x => x.ClientOrderId == id).Id;
        _f.Fake.Fill(remoteId, 99m);
        Assert.Equal(HttpStatusCode.OK, (await admin.Post($"/api/brokers/{brokerId}/sync")).StatusCode);
        var list = await admin.Get("/api/orders?q=" + id);
        var item = list.GetProperty("items")[0];
        Assert.Equal("filled", item.GetProperty("status").GetString());
        Assert.Equal(4m, item.GetProperty("filled").GetDecimal());

        // a filled order is not cancellable
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post($"/api/orders/{id}/cancel")).StatusCode);

        // the bot's ledger now holds the shares
        var bot = await admin.Get("/api/bots/" + botId);
        var pos = bot.GetProperty("positions")[0];
        Assert.Equal("NVDA", pos.GetProperty("symbol").GetString());
        Assert.Equal(4m, pos.GetProperty("qty").GetDecimal());
        Assert.Equal(99m, pos.GetProperty("avg").GetDecimal());

        // open order -> cancel
        var o2 = await Session.Json(await admin.Post("/api/orders", new { brokerId, symbol = "AMD", side = "sell", type = "market", qty = 1 }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Post($"/api/orders/{o2.GetProperty("id").GetString()}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post($"/api/orders/{o2.GetProperty("id").GetString()}/cancel")).StatusCode);
    }

    [Fact]
    public async Task Broker_rejection_and_bad_input_do_not_create_orders()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var before = (await admin.Get("/api/orders")).GetProperty("counts").GetProperty("all").GetInt32();
        var r = await admin.Post("/api/orders", new { brokerId, symbol = "REJECT", side = "buy", type = "market", qty = 1 });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("buying power", (await Session.Json(r)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/orders", new { brokerId, symbol = "AAPL", side = "buy", type = "limit", qty = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/orders", new { brokerId = "nope", symbol = "AAPL", side = "buy", type = "market", qty = 1 })).StatusCode);
        Assert.Equal(before, (await admin.Get("/api/orders")).GetProperty("counts").GetProperty("all").GetInt32());
    }

    [Fact]
    public async Task Orders_placed_outside_the_app_are_imported_as_external()
    {
        var (admin, brokerId) = await AdminWithBroker();
        _f.Fake.AddExternal("ZZZZ", 7);
        await admin.Post($"/api/brokers/{brokerId}/sync");
        var o = (await admin.Get("/api/orders?q=ZZZZ")).GetProperty("items")[0];
        Assert.Equal("external", o.GetProperty("source").GetString());
        Assert.Equal("External", o.GetProperty("strategy").GetString());
    }

    [Fact]
    public async Task Pause_and_resume_a_bot_and_cancel_all()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var botId = await CreateBot(admin, brokerId);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Post($"/api/bots/{botId}/pause")).StatusCode);
        Assert.Equal("paused", (await admin.Get("/api/bots/" + botId)).GetProperty("bot").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Locked, (await admin.Post("/api/orders", new { brokerId, botId, symbol = "AAPL", side = "buy", type = "market", qty = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Post($"/api/bots/{botId}/resume")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Post("/api/bots/zzz/pause")).StatusCode);

        await admin.Post("/api/orders", new { brokerId, symbol = "AAPL", side = "buy", type = "limit", qty = 1, limitPrice = 1 });
        var r = await Session.Json(await admin.Post("/api/orders/cancel-open"));
        Assert.True(r.GetProperty("cancelled").GetInt32() >= 1);
        Assert.Equal(0, (await admin.Get("/api/orders")).GetProperty("counts").GetProperty("open").GetInt32());
    }

    [Fact]
    public async Task Csv_export_neutralises_formula_injection()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var botId = await CreateBot(admin, brokerId, "=HYPERLINK(\"http://evil\")");
        await admin.Post("/api/orders", new { brokerId, botId, symbol = "CSVT", side = "buy", type = "market", qty = 1 });
        var csv = await admin.Http.GetStringAsync("/api/orders/export.csv?q=CSVT");
        Assert.StartsWith("id,time_utc,symbol", csv);
        Assert.Contains("CSVT", csv);
        Assert.Contains("'=HYPERLINK", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
    }

    [Fact]
    public async Task Closing_a_position_goes_to_the_broker()
    {
        var (admin, brokerId) = await AdminWithBroker();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Post("/api/positions/close", new { brokerId, symbol = "BTC/USD" })).StatusCode);
        Assert.DoesNotContain(_f.Fake.Positions, p => p.Symbol == "BTC/USD");
        _f.Fake.Positions.Add(new("BTC/USD", 1, 60000m, 61000m, 61000m, 1000m, 0.016m, "crypto"));
    }

    // ---------- bots ----------
    [Fact]
    public async Task Bot_crud_validation_and_delete_keeps_history()
    {
        var (admin, brokerId) = await AdminWithBroker();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/bots", new { name = "", brokerId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/bots", new { name = "x", brokerId = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/bots", new { name = "x", brokerId, allocationPct = -5 })).StatusCode);
        var id = await CreateBot(admin, brokerId, "Edit me", 0.5m);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Put("/api/bots/" + id, new { name = "Renamed", @class = "ETFs", description = "d", allocationPct = 0.75 })).StatusCode);
        var b = (await admin.Get("/api/bots/" + id)).GetProperty("bot");
        Assert.Equal("Renamed", b.GetProperty("name").GetString());
        Assert.Equal(750m, b.GetProperty("allocated").GetDecimal());
        var key = await CreateKey(admin, brokerId, id, "read", "trade");
        var order = await Session.Json(await Session.Bearer(_f, key).Post("/v1/orders", new { symbol = "KEEP", side = "buy", type = "market", qty = 1 }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Delete("/api/bots/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Bearer(_f, key).Http.GetAsync("/v1/account")).StatusCode); // its key died with it
        var kept = (await admin.Get("/api/orders?q=" + order.GetProperty("id").GetString())).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("botId").ValueKind);
    }

    [Fact]
    public async Task Broker_with_bots_cannot_be_removed()
    {
        var (admin, brokerId) = await AdminWithBroker();
        await CreateBot(admin, brokerId);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Delete("/api/brokers/" + brokerId)).StatusCode);
    }

    // ---------- API keys + public API ----------
    [Fact]
    public async Task Key_validation_and_rotation()
    {
        var (admin, brokerId) = await AdminWithBroker();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/keys", new { name = "", brokerId, perms = new[] { "read" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/keys", new { name = "x", brokerId, perms = new[] { "root" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/keys", new { name = "x", brokerId = "nope", perms = new[] { "read" } })).StatusCode);
        var created = await Session.Json(await admin.Post("/api/keys", new { name = "rot", brokerId, perms = new[] { "read" } }));
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("ot_test_", secret);
        Assert.DoesNotContain(secret, (await admin.Http.GetStringAsync("/api/keys")));   // the secret is never listed again
        Assert.Equal(HttpStatusCode.OK, (await Session.Bearer(_f, secret).Http.GetAsync("/v1/account")).StatusCode);
        var id = created.GetProperty("key").GetProperty("id").GetString();
        var rotated = (await Session.Json(await admin.Post($"/api/keys/{id}/rotate"))).GetProperty("secret").GetString()!;
        Assert.NotEqual(secret, rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Bearer(_f, secret).Http.GetAsync("/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Session.Bearer(_f, rotated).Http.GetAsync("/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Delete("/api/keys/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Bearer(_f, rotated).Http.GetAsync("/v1/account")).StatusCode);
    }

    [Fact]
    public async Task Public_api_requires_a_valid_key_and_the_right_permission()
    {
        var (admin, brokerId) = await AdminWithBroker();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Anonymous(_f).Http.GetAsync("/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Session.Bearer(_f, "ot_test_garbage").Http.GetAsync("/v1/account")).StatusCode);
        var readOnly = Session.Bearer(_f, await CreateKey(admin, brokerId, null, "read"));
        Assert.Equal(HttpStatusCode.OK, (await readOnly.Http.GetAsync("/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.Http.GetAsync("/v1/positions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.Post("/v1/orders", new { symbol = "AAPL", side = "buy", type = "market", qty = 1 })).StatusCode);
        // the cookie session is not accepted on /v1 and a key is not accepted on /api
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.Http.GetAsync("/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await readOnly.Http.GetAsync("/api/dashboard")).StatusCode);
        // X-API-Key header works too
        var secret = await CreateKey(admin, brokerId, null, "read");
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-API-Key", secret);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/v1/account")).StatusCode);
    }

    [Fact]
    public async Task Public_api_account_orders_and_positions()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var key = Session.Bearer(_f, await CreateKey(admin, brokerId, null, "read", "positions", "trade"));
        var acc = await key.Get("/v1/account");
        Assert.Equal(100_000m, acc.GetProperty("equity").GetDecimal());
        Assert.True(acc.GetProperty("paper").GetBoolean());
        var pos = await key.Get("/v1/positions");
        Assert.Contains(pos.EnumerateArray(), p => p.GetProperty("symbol").GetString() == "AAPL");

        var r = await key.Post("/v1/orders", new { symbol = "tsla", side = "buy", type = "limit", qty = 3, limitPrice = 250.5 });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var o = await Session.Json(r);
        var id = o.GetProperty("id").GetString();
        Assert.Equal("TSLA", o.GetProperty("symbol").GetString());
        Assert.Equal("pending", o.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await key.Http.GetAsync("/v1/orders/" + id)).StatusCode);
        Assert.Contains((await key.Get("/v1/orders?status=open")).EnumerateArray(), x => x.GetProperty("id").GetString() == id);
        Assert.Equal(HttpStatusCode.BadRequest, (await key.Post("/v1/orders", new { symbol = "TSLA", side = "buy", type = "limit", qty = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await key.Delete("/v1/orders/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await key.Delete("/v1/orders/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await key.Http.GetAsync("/v1/orders/OT-NOPE")).StatusCode);
    }

    [Fact]
    public async Task Public_api_serves_price_bars_with_validation()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var key = Session.Bearer(_f, await CreateKey(admin, brokerId, null, "read"));
        var bars = await key.Get("/v1/bars/aapl?timeframe=1Day&limit=3");
        Assert.Equal(3, bars.GetArrayLength());
        Assert.Equal(10.5m, bars[0].GetProperty("close").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await key.Http.GetAsync("/v1/bars/AAPL?timeframe=2Weeks")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await key.Http.GetAsync("/v1/bars/AA%3BPL")).StatusCode);
        var noRead = Session.Bearer(_f, await CreateKey(admin, brokerId, null, "trade"));
        Assert.Equal(HttpStatusCode.Forbidden, (await noRead.Http.GetAsync("/v1/bars/AAPL")).StatusCode);
    }

    [Fact]
    public async Task Bot_bound_key_is_scoped_attributed_and_respects_pause()
    {
        var (admin, brokerId) = await AdminWithBroker();
        var botA = await CreateBot(admin, brokerId);
        var botB = await CreateBot(admin, brokerId);
        var keyA = Session.Bearer(_f, await CreateKey(admin, brokerId, botA, "read", "positions", "trade"));
        var keyB = Session.Bearer(_f, await CreateKey(admin, brokerId, botB, "read", "trade"));

        var oA = await Session.Json(await keyA.Post("/v1/orders", new { symbol = "AAA", side = "buy", type = "market", qty = 2 }));
        var idA = oA.GetProperty("id").GetString();
        Assert.Equal(JsonValueKind.Array, (await keyA.Get("/v1/orders")).ValueKind);
        // B cannot see or cancel A's order
        Assert.Equal(HttpStatusCode.NotFound, (await keyB.Http.GetAsync("/v1/orders/" + idA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await keyB.Delete("/v1/orders/" + idA)).StatusCode);
        Assert.DoesNotContain((await keyB.Get("/v1/orders")).EnumerateArray(), x => x.GetProperty("id").GetString() == idA);
        // the dashboard attributes it to bot A and the heartbeat is recorded
        var ui = (await admin.Get("/api/orders?q=" + idA)).GetProperty("items")[0];
        Assert.Equal(botA, ui.GetProperty("botId").GetString());
        Assert.Equal("api", ui.GetProperty("source").GetString());
        Assert.NotEqual(JsonValueKind.Null, (await admin.Get("/api/bots/" + botA)).GetProperty("bot").GetProperty("heartbeat").ValueKind);
        Assert.True((await admin.Get("/api/bots/" + botA)).GetProperty("bot").GetProperty("online").GetBoolean());

        // fill it and the bound key's positions reflect the bot's holdings only
        _f.Fake.Fill(_f.Fake.Orders.First(x => x.ClientOrderId == idA).Id, 5m);
        await admin.Post($"/api/brokers/{brokerId}/sync");
        var pos = (await keyA.Get("/v1/positions")).EnumerateArray().Single();
        Assert.Equal(("AAA", 2m), (pos.GetProperty("symbol").GetString(), pos.GetProperty("qty").GetDecimal()));

        // paused bot -> orders refused with 423
        await admin.Post($"/api/bots/{botA}/pause");
        Assert.Equal(HttpStatusCode.Locked, (await keyA.Post("/v1/orders", new { symbol = "AAA", side = "buy", type = "market", qty = 1 })).StatusCode);
        var hb = await Session.Json(await keyA.Post("/v1/heartbeat"));
        Assert.False(hb.GetProperty("running").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await keyA.Http.GetAsync("/v1/bot")).StatusCode);
    }

    [Fact]
    public async Task Static_site_is_served()
    {
        var r = await Session.Anonymous(_f).Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("html", r.Content.Headers.ContentType?.MediaType);
    }
}
