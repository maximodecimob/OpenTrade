using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace OpenTrade.Api;

public sealed record Snapshot(Broker Broker, AccountInfo? Account, List<PositionInfo> Positions, string? Error);

/// <summary>Creates broker gateways (decrypting secrets) and caches the read-only calls for a few seconds.</summary>
public sealed class BrokerHub(IBrokerFactory factory, IDataProtectionProvider dp, IMemoryCache cache)
{
    readonly IDataProtector _p = dp.CreateProtector("OpenTrade.BrokerSecret.v1");
    static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    public string Protect(string secret) => _p.Protect(secret);
    public IBrokerGateway Gateway(Broker b) => factory.Create(b, _p.Unprotect(b.SecretProtected));
    public IBrokerGateway Gateway(Broker b, string secret) => factory.Create(b, secret);

    public void Invalidate(string brokerId)
    {
        cache.Remove("acct:" + brokerId);
        cache.Remove("pos:" + brokerId);
    }

    /// <summary>Caches the running task itself, so concurrent callers share one broker call. A failed call is evicted right away.</summary>
    Task<T> Cached<T>(string key, TimeSpan ttl, Func<Task<T>> load)
    {
        var t = cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = ttl; return load(); })!;
        if (!t.IsCompleted)
            _ = t.ContinueWith(x => { if (x.IsFaulted || x.IsCanceled) cache.Remove(key); }, TaskScheduler.Default);
        else if (t.IsFaulted || t.IsCanceled) cache.Remove(key);
        return t;
    }

    /// <summary>Latest known price of a symbol (1-minute bar, falling back to the daily bar).</summary>
    public async Task<decimal?> LastPrice(Broker b, string symbol)
    {
        var bars = await Bars(b, symbol, "1Min", 1);
        if (bars.Count == 0) bars = await Bars(b, symbol, "1Day", 1);
        return bars.Count == 0 ? null : bars[^1].Close;
    }

    public Task<AccountInfo> Account(Broker b) => Cached("acct:" + b.Id, Short, () => Gateway(b).GetAccount());
    public Task<List<PositionInfo>> Positions(Broker b) => Cached("pos:" + b.Id, Short, () => Gateway(b).GetPositions());
    public Task<History> History(Broker b, string period, string tf) => Cached($"hist:{b.Id}:{period}:{tf}", TimeSpan.FromSeconds(60), () => Gateway(b).GetHistory(period, tf));
    public Task<List<Bar>> Bars(Broker b, string symbol, string tf, int limit) => Cached($"bars:{b.Id}:{symbol}:{tf}:{limit}", TimeSpan.FromSeconds(15), () => Gateway(b).GetBars(symbol, tf, limit));
    public Task<List<AssetInfo>> Assets(Broker b) => Cached("assets:" + b.Paper, TimeSpan.FromHours(1), () => Gateway(b).GetAssets());

    public async Task<List<Snapshot>> Snapshots(IEnumerable<Broker> brokers) => [.. await Task.WhenAll(brokers.Select(async b =>
    {
        try
        {
            var a = Account(b);
            var p = Positions(b);
            await Task.WhenAll(a, p);
            return new Snapshot(b, a.Result, p.Result, null);
        }
        catch (BrokerException e) { return new Snapshot(b, null, [], e.Message); }
        catch (CryptographicException) { return new Snapshot(b, null, [], "Stored credentials cannot be decrypted. Reconnect this broker."); }
    }))];
}

/// <summary>Turns each bot's percentage into USD, using the equity of its broker account.</summary>
public static class Alloc
{
    public const decimal MaxPct = 100m;

    public static void Apply(IEnumerable<Bot> bots, IReadOnlyDictionary<string, decimal> equityByBroker)
    {
        foreach (var b in bots) b.Allocated = Math.Round(equityByBroker.GetValueOrDefault(b.BrokerId) * b.AllocPct / 100m, 2);
    }

    public static Dictionary<string, decimal> Equity(IEnumerable<Snapshot> snaps) =>
        snaps.Where(s => s.Account is not null).ToDictionary(s => s.Broker.Id, s => s.Account!.Equity);

    /// <summary>Null when the percentages fit, otherwise the message to show.</summary>
    public static string? Check(IEnumerable<Bot> bots, Func<Bot, decimal> pct)
    {
        foreach (var g in bots.GroupBy(b => b.BrokerId))
        {
            var total = g.Sum(pct);
            if (total > MaxPct) return $"Allocations on one broker account cannot exceed 100% (this would be {total:0.##}%)";
        }
        return null;
    }
}

public sealed record Activity(long Time, string Broker, string Event, bool Ok);

public sealed class EventLog
{
    readonly ConcurrentQueue<Activity> _q = new();
    public void Add(string broker, string ev, bool ok = true)
    {
        _q.Enqueue(new(Clock.Now, broker, ev, ok));
        while (_q.Count > 50) _q.TryDequeue(out _);
    }
    public Activity[] Latest(int n) => [.. _q.Reverse().Take(n)];
}

public sealed record OrderRequest(string? Symbol, string? Side, string? Type, decimal? Qty, decimal? LimitPrice, string? TimeInForce, string? ClientOrderId = null);

public sealed class OrderService(OtDb db, BrokerHub hub, EventLog log, OrderSync sync)
{
    static readonly Regex SymbolRx = new("^[A-Z0-9./-]{1,15}$", RegexOptions.Compiled);
    static readonly Regex ClientIdRx = new("^[A-Za-z0-9_-]{8,48}$", RegexOptions.Compiled);
    // One order at a time per bot, so two parallel requests cannot both pass the capital check.
    static readonly ConcurrentDictionary<string, SemaphoreSlim> BotLocks = new();

    public static string NewId() => "OT-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    /// <summary>The id the caller chose (idempotency key) when it is valid, otherwise a fresh one. Null when the supplied id is malformed.</summary>
    public static string? ChooseId(string? supplied) => string.IsNullOrWhiteSpace(supplied) ? NewId() : ClientIdRx.IsMatch(supplied.Trim()) ? supplied.Trim() : null;

    public static (NewOrder? Order, string? Error) Validate(OrderRequest r, string clientId)
    {
        var symbol = r.Symbol?.Trim().ToUpperInvariant() ?? "";
        if (!SymbolRx.IsMatch(symbol)) return (null, "Invalid symbol");
        var side = r.Side?.ToLowerInvariant();
        if (side is not ("buy" or "sell")) return (null, "Side must be buy or sell");
        var type = (r.Type ?? "market").ToLowerInvariant();
        if (type is not ("market" or "limit")) return (null, "Type must be market or limit");
        if (r.Qty is not > 0 or > 1_000_000) return (null, "Quantity must be greater than 0");
        if (type == "limit" && r.LimitPrice is not > 0) return (null, "A limit order needs a limit price");
        var tif = (r.TimeInForce ?? "day").ToLowerInvariant();
        if (tif is not ("day" or "gtc")) return (null, "Time in force must be day or gtc");
        return (new NewOrder(symbol, side == "buy" ? Side.Buy : Side.Sell, type == "limit" ? OrderType.Limit : OrderType.Market,
            r.Qty.Value, type == "limit" ? Math.Round(r.LimitPrice!.Value, 4) : null, tif, clientId), null);
    }

    /// <summary>A bot may only spend its allocation and only sell what it holds. Throws a 422 BrokerException (answered as 409) otherwise.</summary>
    async Task CheckBotLimits(Broker broker, Bot bot, NewOrder n, CancellationToken ct)
    {
        var mine = await db.Orders.AsNoTracking().Where(o => o.BotId == bot.Id && (o.Filled > 0 || o.Status == OrderStatus.Pending || o.Status == OrderStatus.Partial)).ToListAsync(ct);
        var held = Ledger.Compute(mine.Where(o => o.Filled > 0), new Dictionary<string, decimal>()).Positions;
        if (n.Side == Side.Sell)
        {
            var have = held.FirstOrDefault(p => p.Symbol == n.Symbol)?.Qty ?? 0;
            var selling = mine.Where(o => o.Open && o.Side == Side.Sell && o.Symbol == n.Symbol).Sum(o => o.Qty - o.Filled);
            if (n.Qty > have - selling)
                throw new BrokerException($"The bot holds {Math.Max(0, have - selling):0.####} {n.Symbol} that is not already being sold, so it cannot sell {n.Qty:0.####}", 422);
            return;
        }
        var acc = await hub.Account(broker);
        var allocated = Math.Round(acc.Equity * bot.AllocPct / 100m, 2);
        if (allocated <= 0) throw new BrokerException("This bot has no capital allocated. Give it a percentage in Strategies & Bots first", 422);
        var price = n.LimitPrice ?? await hub.LastPrice(broker, n.Symbol) ?? throw new BrokerException("No price available for " + n.Symbol + ", so the order cannot be checked against the allocation", 422);
        var committed = held.Sum(p => p.Qty * p.Avg);
        foreach (var o in mine.Where(o => o.Open && o.Side == Side.Buy))
            committed += (o.Qty - o.Filled) * (o.LimitPrice ?? await hub.LastPrice(broker, o.Symbol) ?? 0m);
        var cost = n.Qty * price;
        if (committed + cost > allocated + 0.005m)
            throw new BrokerException($"Order exceeds the bot's allocation: it would use {committed + cost:N2} of {allocated:N2} USD ({bot.AllocPct:0.##}% of the account)", 422);
    }

    public async Task<Order> Place(Broker broker, Bot? bot, NewOrder n, string source, CancellationToken ct = default)
    {
        var gate = bot is null ? null : BotLocks.GetOrAdd(bot.Id, _ => new SemaphoreSlim(1, 1));
        if (gate is not null) await gate.WaitAsync(ct);
        try
        {
            if (bot is not null) await CheckBotLimits(broker, bot, n, ct);
            return await PlaceUnchecked(broker, bot, n, source, ct);
        }
        finally { gate?.Release(); }
    }

    async Task<Order> PlaceUnchecked(Broker broker, Bot? bot, NewOrder n, string source, CancellationToken ct)
    {
        var remote = await hub.Gateway(broker).PlaceOrder(n, ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.BrokerOrderId == remote.Id, ct);
        if (order is null)
        {
            order = new Order { Id = n.ClientOrderId, BrokerOrderId = remote.Id, BrokerId = broker.Id };
            db.Orders.Add(order);
        }
        order.BotId = bot?.Id;
        order.Source = source;
        Apply(order, remote);
        order.Symbol = n.Symbol; order.Side = n.Side; order.Type = n.Type; order.Qty = n.Qty; order.Tif = n.Tif;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // The background sync inserted the same broker order first: keep that row, add our attribution.
            db.ChangeTracker.Clear();
            var existing = await db.Orders.FirstAsync(o => o.BrokerOrderId == remote.Id, ct);
            existing.BotId = bot?.Id; existing.Source = source;
            await db.SaveChangesAsync(ct);
            order = existing;
        }
        hub.Invalidate(broker.Id);
        log.Add(broker.Name, $"Order placed: {n.Side} {n.Qty} {n.Symbol}");
        return order;
    }

    public static void Apply(Order o, RemoteOrder r)
    {
        o.Symbol = r.Symbol; o.Side = r.Side; o.Type = r.Type; o.Qty = r.Qty; o.Filled = r.Filled;
        o.LimitPrice = r.LimitPrice; o.AvgFill = r.AvgFill; o.Status = r.Status;
        if (r.Tif.Length > 0) o.Tif = r.Tif;
        o.CreatedMs = r.CreatedAt.ToUnixTimeMilliseconds();
        o.UpdatedMs = r.UpdatedAt.ToUnixTimeMilliseconds();
    }

    /// <summary>Returns null on success or a message when the broker no longer lets the order be cancelled.</summary>
    public async Task<string?> Cancel(Order o, CancellationToken ct = default)
    {
        var broker = await db.Brokers.FindAsync([o.BrokerId], ct) ?? throw new BrokerException("Broker no longer exists", 404);
        try { await hub.Gateway(broker).CancelOrder(o.BrokerOrderId, ct); }
        catch (BrokerException e) when (e.Status is 422 or 404)
        {
            await sync.SyncBroker(broker.Id, ct);
            return "Order can no longer be cancelled";
        }
        o.Status = OrderStatus.Cancelled;
        o.UpdatedMs = Clock.Now;
        await db.SaveChangesAsync(ct);
        hub.Invalidate(broker.Id);
        log.Add(broker.Name, "Order cancelled: " + o.Symbol);
        return null;
    }

    public async Task<int> CancelAll(Broker broker, CancellationToken ct = default)
    {
        var n = await hub.Gateway(broker).CancelAllOrders(ct);
        await sync.SyncBroker(broker.Id, ct);
        hub.Invalidate(broker.Id);
        log.Add(broker.Name, $"Cancelled {n} open orders");
        return n;
    }
}

/// <summary>Pulls order state from every broker into the database every few seconds, so fills made outside this app show up too.</summary>
public sealed class OrderSync(IServiceScopeFactory sf, ILogger<OrderSync> logger, IConfiguration cfg) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (cfg["Sync:Enabled"] == "false") return;
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await SyncAll(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Order sync failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    public async Task SyncAll(CancellationToken ct = default)
    {
        string[] ids;
        using (var scope = sf.CreateScope())
            ids = await scope.ServiceProvider.GetRequiredService<OtDb>().Brokers.Select(b => b.Id).ToArrayAsync(ct);
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (id, t) => await SyncBroker(id, t));
    }

    public async Task SyncBroker(string brokerId, CancellationToken ct = default)
    {
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OtDb>();
        var hub = scope.ServiceProvider.GetRequiredService<BrokerHub>();
        var log = scope.ServiceProvider.GetRequiredService<EventLog>();
        var b = await db.Brokers.FirstOrDefaultAsync(x => x.Id == brokerId, ct);
        if (b is null) return;
        var changed = false;
        try
        {
            var remote = await hub.Gateway(b).GetOrders(ct);
            var ids = remote.Select(r => r.Id).ToList();
            var existing = (await db.Orders.Where(o => ids.Contains(o.BrokerOrderId)).ToListAsync(ct)).ToDictionary(o => o.BrokerOrderId);
            foreach (var r in remote)
            {
                if (existing.TryGetValue(r.Id, out var o))
                {
                    if (o.Status != r.Status || o.Filled != r.Filled || o.AvgFill != r.AvgFill)
                    {
                        changed = true;
                        OrderService.Apply(o, r);
                    }
                }
                else
                {
                    changed = true;
                    var n = new Order { BrokerOrderId = r.Id, BrokerId = b.Id, Source = "external" };
                    OrderService.Apply(n, r);
                    db.Orders.Add(n);
                }
            }
            if (b.LastError is not null) log.Add(b.Name, "Connection restored");
            b.LastError = null;
        }
        catch (BrokerException e)
        {
            if (b.LastError != e.Message) log.Add(b.Name, "Sync failed: " + e.Message, false);
            b.LastError = e.Message;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            b.LastError = "Stored credentials cannot be decrypted. Reconnect this broker.";
        }
        b.LastSyncMs = Clock.Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) { logger.LogWarning(e, "Order sync could not save"); }
        if (changed) hub.Invalidate(b.Id);
    }
}

public sealed record BotPosition(string Symbol, decimal Qty, decimal Avg, decimal Last, decimal Value, decimal Pnl);
public sealed record BotStats(List<BotPosition> Positions, decimal Invested, decimal Realized, decimal Unrealized)
{
    public decimal Pnl => Realized + Unrealized;
}

/// <summary>Rebuilds what a bot holds and has earned from the fills attributed to it (average-cost method, long positions).</summary>
public static class Ledger
{
    public static BotStats Compute(IEnumerable<Order> botOrders, IReadOnlyDictionary<string, decimal> prices)
    {
        var book = new Dictionary<string, (decimal Qty, decimal Avg)>();
        decimal realized = 0;
        foreach (var o in botOrders.Where(x => x.Filled > 0 && x.Price > 0).OrderBy(x => x.CreatedMs))
        {
            var (qty, avg) = book.GetValueOrDefault(o.Symbol);
            if (o.Side == Side.Buy)
            {
                var total = qty + o.Filled;
                avg = total == 0 ? 0 : (avg * qty + o.Price * o.Filled) / total;
                qty = total;
            }
            else
            {
                var close = Math.Min(o.Filled, qty);
                realized += (o.Price - avg) * close;
                qty -= close;
            }
            book[o.Symbol] = (qty, avg);
        }
        var pos = book.Where(kv => kv.Value.Qty > 0).Select(kv =>
        {
            var last = prices.TryGetValue(kv.Key, out var p) && p > 0 ? p : kv.Value.Avg;
            return new BotPosition(kv.Key, kv.Value.Qty, kv.Value.Avg, last, kv.Value.Qty * last, kv.Value.Qty * (last - kv.Value.Avg));
        }).OrderBy(p => p.Symbol).ToList();
        return new(pos, pos.Sum(p => p.Value), realized, pos.Sum(p => p.Pnl));
    }
}
