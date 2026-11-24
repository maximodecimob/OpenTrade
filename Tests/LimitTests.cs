using System.Net;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace OpenTrade.Tests;

/// <summary>A server with tiny limits, so the 429 answers can be checked quickly. The real values are measured by the LoadTests project.</summary>
public sealed class TinyLimitsFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.UseSetting("RateLimit:V1PerMinute", "5");
        b.UseSetting("RateLimit:GlobalPerMinute", "1000");
        b.UseSetting("Keys:MaxCount", "2");
    }
}

public class LimitTests : IClassFixture<TinyLimitsFactory>
{
    readonly TinyLimitsFactory _f;
    public LimitTests(TinyLimitsFactory f) => _f = f;

    async Task<(Session Admin, string BrokerId)> Setup()
    {
        var admin = await Session.Login(_f);
        var list = await admin.Get("/api/brokers");
        if (list.GetArrayLength() > 0) return (admin, list[0].GetProperty("id").GetString()!);
        var r = await admin.Post("/api/brokers", new { name = "Alpaca Paper", keyId = FakeBroker.GoodKeyId, secret = FakeBroker.GoodSecret, paper = true });
        return (admin, (await Session.Json(r)).GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task Configured_limits_are_applied()
    {
        var (admin, brokerId) = await Setup();
        var made = await admin.Post("/api/keys", new { name = "k1", brokerId, perms = new[] { "read" }, expiresInDays = 1 });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var key = (await Session.Json(made)).GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.Post("/api/keys", new { name = "k2", brokerId, perms = new[] { "read" }, expiresInDays = 1 })).StatusCode);

        // Keys:MaxCount = 2: the third key is refused
        var third = await admin.Post("/api/keys", new { name = "k3", brokerId, perms = new[] { "read" }, expiresInDays = 1 });
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

        // RateLimit:V1PerMinute = 5: the sixth request of the minute gets 429
        var bot = Session.Bearer(_f, key);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) codes.Add((await bot.Http.GetAsync("/v1/account")).StatusCode);
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(3, codes.Count(c => c == (HttpStatusCode)429));
    }
}

/// <summary>Per-IP limit of only 8 requests, to show that bots are not affected by it (they are limited per key).</summary>
public sealed class PerBotLimitFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.UseSetting("RateLimit:GlobalPerMinute", "8");
        b.UseSetting("RateLimit:V1PerMinute", "50");
        b.UseSetting("RateLimit:BadKeyPerMinute", "3");
    }
}

public class PerBotLimitTests : IClassFixture<PerBotLimitFactory>
{
    readonly PerBotLimitFactory _f;
    public PerBotLimitTests(PerBotLimitFactory f) => _f = f;

    [Fact]
    public async Task Bots_are_limited_per_key_not_per_ip_and_wrong_keys_per_ip()
    {
        // 4 web requests, under the per-IP limit of 8
        var admin = await Session.Login(_f);
        var broker = await admin.Post("/api/brokers", new { name = "Alpaca Paper", keyId = FakeBroker.GoodKeyId, secret = FakeBroker.GoodSecret, paper = true });
        var brokerId = (await Session.Json(broker)).GetProperty("id").GetString()!;
        var made = await admin.Post("/api/keys", new { name = "k", brokerId, perms = new[] { "read" }, expiresInDays = 1 });
        var key = (await Session.Json(made)).GetProperty("secret").GetString()!;

        // 20 bot requests from the same IP: more than the per-IP limit (8), all OK because only the key limit (50) counts
        var bot = Session.Bearer(_f, key);
        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.OK, (await bot.Http.GetAsync("/v1/account")).StatusCode);

        // wrong keys: 3 tries per minute per IP, then 429
        var wrong = Session.Bearer(_f, "ot_test_this-key-does-not-exist");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await wrong.Http.GetAsync("/v1/account")).StatusCode);
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, (HttpStatusCode)429, (HttpStatusCode)429], codes);
    }
}
