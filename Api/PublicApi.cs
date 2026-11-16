using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace OpenTrade.Api;

/// <summary>
/// REST API for external bots, authenticated with an API key (Authorization: Bearer ot_... or X-API-Key).
/// A key belongs to one broker account and optionally to one bot; a bot-bound key only sees and controls that bot's orders.
/// </summary>
public static class PublicApi
{
    sealed record Caller(ApiKey Key, Broker Broker, Bot? Bot);

    static string? Secret(HttpContext c)
    {
        var h = c.Request.Headers.Authorization.ToString();
        if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return h[7..].Trim();
        var x = c.Request.Headers["X-API-Key"].ToString();
        return x.Length > 0 ? x.Trim() : null;
    }

    public static string RateKey(HttpContext c) => Secret(c) is { Length: > 0 } s ? Convert.ToHexString(Endpoints.HashKey(s)) : c.Connection.RemoteIpAddress?.ToString() ?? "?";

    static async Task<(Caller? Caller, IResult? Fail)> Gate(HttpContext c, OtDb db, string perm)
    {
        var s = Secret(c);
        if (s is null || s.Length > 200) return (null, Endpoints.Err(401, "Missing API key. Send it as 'Authorization: Bearer <key>'."));
        var hash = Endpoints.HashKey(s);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Hash == hash);
        if (key is null || key.ExpiresMs < Clock.Now) return (null, Endpoints.Err(401, "Invalid or expired API key"));
        if (!key.PermList.Contains(perm)) return (null, Endpoints.Err(403, $"This key does not have the '{perm}' permission"));
        var broker = await db.Brokers.FirstOrDefaultAsync(b => b.Id == key.BrokerId);
        if (broker is null) return (null, Endpoints.Err(401, "The broker account of this key no longer exists"));
        var bot = key.BotId is null ? null : await db.Bots.FirstOrDefaultAsync(b => b.Id == key.BotId);
        key.LastUsedMs = Clock.Now;
        key.Requests++;
        if (bot is not null) bot.LastHeartbeatMs = Clock.Now;
        await db.SaveChangesAsync();
        return (new Caller(key, broker, bot), null);
    }

    static object OrderDto(Order o) => new
    {
        o.Id, o.Symbol, Side = o.Side.ToString().ToLowerInvariant(), Type = o.Type.ToString().ToLowerInvariant(), o.Qty, o.Filled, o.LimitPrice,
        avgFillPrice = o.AvgFill, Status = o.Status.ToString().ToLowerInvariant(), timeInForce = o.Tif,
        createdAt = DateTimeOffset.FromUnixTimeMilliseconds(o.CreatedMs).ToString("o", CultureInfo.InvariantCulture),
    };

    static IQueryable<Order> Scope(OtDb db, Caller a) =>
        a.Bot is null ? db.Orders.Where(o => o.BrokerId == a.Broker.Id) : db.Orders.Where(o => o.BotId == a.Bot.Id);

    public static void MapPublicApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1").RequireRateLimiting("v1");

        v1.MapGet("/account", async (HttpContext c, OtDb db, BrokerHub hub) =>
        {
            var (a, fail) = await Gate(c, db, "read");
            if (a is null) return fail!;
            var acc = await hub.Account(a.Broker);
            return Results.Ok(new
            {
                broker = a.Broker.Name, paper = a.Broker.Paper, accountNumber = acc.Number, acc.Status, acc.Equity, acc.Cash, acc.BuyingPower,
                bot = a.Bot is null ? null : new { a.Bot.Id, a.Bot.Name, a.Bot.Running, a.Bot.Allocated },
            });
        });

        v1.MapGet("/bot", async (HttpContext c, OtDb db) =>
        {
            var (a, fail) = await Gate(c, db, "read");
            if (a is null) return fail!;
            return a.Bot is null ? Endpoints.Err(404, "This key is not bound to a bot") : Results.Ok(new { a.Bot.Id, a.Bot.Name, a.Bot.Running, a.Bot.Allocated, serverTime = DateTimeOffset.UtcNow });
        });

        v1.MapPost("/heartbeat", async (HttpContext c, OtDb db) =>
        {
            var (a, fail) = await Gate(c, db, "read");
            if (a is null) return fail!;
            return Results.Ok(new { running = a.Bot?.Running ?? true, serverTime = DateTimeOffset.UtcNow });
        });

        v1.MapGet("/positions", async (HttpContext c, OtDb db, BrokerHub hub) =>
        {
            var (a, fail) = await Gate(c, db, "positions");
            if (a is null) return fail!;
            var remote = await hub.Positions(a.Broker);
            if (a.Bot is null)
                return Results.Ok(remote.Select(p => new { p.Symbol, p.Qty, avgEntryPrice = p.AvgEntry, currentPrice = p.Last, p.MarketValue, p.UnrealizedPl }));
            var prices = remote.ToDictionary(p => p.Symbol, p => p.Last);
            var st = Ledger.Compute(await db.Orders.AsNoTracking().Where(o => o.BotId == a.Bot.Id && o.Filled > 0).ToListAsync(), prices);
            return Results.Ok(st.Positions.Select(p => new { p.Symbol, p.Qty, avgEntryPrice = p.Avg, currentPrice = p.Last, marketValue = p.Value, unrealizedPl = p.Pnl }));
        });

        v1.MapGet("/bars/{symbol}", async (string symbol, HttpContext c, OtDb db, BrokerHub hub, string timeframe = "1Day", int limit = 100) =>
        {
            var (a, fail) = await Gate(c, db, "read");
            if (a is null) return fail!;
            symbol = symbol.Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, "^[A-Z0-9./-]{1,15}$")) return Endpoints.Err(400, "Invalid symbol");
            if (timeframe is not ("1Min" or "5Min" or "15Min" or "1Hour" or "1Day")) return Endpoints.Err(400, "timeframe must be 1Min, 5Min, 15Min, 1Hour or 1Day");
            var bars = await hub.Bars(a.Broker, symbol, timeframe, Math.Clamp(limit, 1, 1000));
            return Results.Ok(bars.Select(b => new
            {
                t = DateTimeOffset.FromUnixTimeMilliseconds(b.T).ToString("o", CultureInfo.InvariantCulture), open = b.Open, high = b.High, low = b.Low, close = b.Close, volume = b.Volume,
            }));
        });

        v1.MapGet("/orders", async (HttpContext c, OtDb db, string? status, int limit = 50) =>
        {
            var (a, fail) = await Gate(c, db, "read");
            if (a is null) return fail!;
            IQueryable<Order> q = Scope(db, a).AsNoTracking();
            if (status == "open") q = q.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Partial);
            else if (Enum.TryParse<OrderStatus>(status, true, out var st)) q = q.Where(o => o.Status == st);
            return Results.Ok((await q.OrderByDescending(o => o.CreatedMs).Take(Math.Clamp(limit, 1, 100)).ToListAsync()).Select(OrderDto));
        });

        v1.MapGet("/orders/{id}", async (string id, HttpContext c, OtDb db) =>
        {
            var (a, fail) = await Gate(c, db, "read");
            if (a is null) return fail!;
            var o = await Scope(db, a).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return o is null ? Endpoints.Err(404, "Order not found") : Results.Ok(OrderDto(o));
        });

        v1.MapPost("/orders", async (OrderRequest r, HttpContext c, OtDb db, OrderService svc) =>
        {
            var (a, fail) = await Gate(c, db, "trade");
            if (a is null) return fail!;
            if (a.Bot is { Running: false }) return Endpoints.Err(423, "Bot is paused");
            var id = "OT-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var (n, error) = OrderService.Validate(r, id);
            if (n is null) return Endpoints.Err(400, error!);
            var o = await svc.Place(a.Broker, a.Bot, n, "api");
            return Results.Json(OrderDto(o), statusCode: 201);
        });

        v1.MapDelete("/orders/{id}", async (string id, HttpContext c, OtDb db, OrderService svc) =>
        {
            var (a, fail) = await Gate(c, db, "trade");
            if (a is null) return fail!;
            var o = await Scope(db, a).FirstOrDefaultAsync(x => x.Id == id);
            if (o is null) return Endpoints.Err(404, "Order not found");
            if (!o.Open) return Endpoints.Err(409, "Order is not open");
            return await svc.Cancel(o) is { } msg ? Endpoints.Err(409, msg) : Results.NoContent();
        });
    }
}
