using System.Collections.Concurrent;
using OpenTrade.Api;

namespace OpenTrade.LoadTests;

/// <summary>
/// A thread-safe, in-memory broker that stands in for Alpaca during load tests, so we measure OpenTrade and not Alpaca
/// (Alpaca's own limit is about 200 requests per minute, far below what we want to test).
/// Every call waits <c>latencyMs</c> to imitate the network round trip to the real broker.
/// Market orders fill at once at 100 USD, limit orders stay open until cancelled.
/// </summary>
public sealed class SimBroker(int latencyMs) : IBrokerGateway
{
    public const decimal Price = 100m;
    readonly Lock _lock = new();
    readonly List<RemoteOrder> _orders = [];
    // A very big account, so the capital rules never refuse orders by accident: we want to measure speed, not the rules.
    static readonly AccountInfo Acc = new("SIM-1", "ACTIVE", 1_000_000_000m, 999_000_000m, 500_000_000m, 1_000_000_000m);
    static readonly List<PositionInfo> Pos =
    [
        new("AAPL", 100, 90m, Price, 100 * Price, 1000m, 0.11m, "us_equity"),
        new("MSFT", 50, 95m, Price, 50 * Price, 250m, 0.05m, "us_equity"),
        new("BTC/USD", 1, 60000m, 61000m, 61000m, 1000m, 0.016m, "crypto"),
    ];

    Task Wait(CancellationToken ct) => latencyMs > 0 ? Task.Delay(latencyMs, ct) : Task.CompletedTask;

    public async Task<AccountInfo> GetAccount(CancellationToken ct = default) { await Wait(ct); return Acc; }
    public async Task<List<PositionInfo>> GetPositions(CancellationToken ct = default) { await Wait(ct); return [.. Pos]; }

    public async Task<List<RemoteOrder>> GetOrders(CancellationToken ct = default)
    {
        await Wait(ct);
        // Like Alpaca's /orders?limit=500&direction=desc: only the newest 500.
        lock (_lock) return [.. Enumerable.Range(0, Math.Min(500, _orders.Count)).Select(i => _orders[_orders.Count - 1 - i])];
    }

    public async Task<RemoteOrder> PlaceOrder(NewOrder o, CancellationToken ct = default)
    {
        await Wait(ct);
        var now = DateTimeOffset.UtcNow;
        var market = o.Type == OrderType.Market;
        var r = new RemoteOrder(Guid.NewGuid().ToString(), o.ClientOrderId, o.Symbol, o.Side, o.Type, o.Qty, market ? o.Qty : 0, o.LimitPrice,
            market ? Price : null, market ? OrderStatus.Filled : OrderStatus.Pending, o.Tif, now, now);
        lock (_lock) _orders.Add(r);
        return r;
    }

    public async Task CancelOrder(string id, CancellationToken ct = default)
    {
        await Wait(ct);
        lock (_lock)
        {
            var i = _orders.FindLastIndex(o => o.Id == id);
            if (i < 0 || _orders[i].Status is not (OrderStatus.Pending or OrderStatus.Partial)) throw new BrokerException("order is not cancelable", 422);
            _orders[i] = _orders[i] with { Status = OrderStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    public async Task<int> CancelAllOrders(CancellationToken ct = default)
    {
        await Wait(ct);
        var n = 0;
        lock (_lock)
            for (var i = 0; i < _orders.Count; i++)
                if (_orders[i].Status is OrderStatus.Pending or OrderStatus.Partial) { _orders[i] = _orders[i] with { Status = OrderStatus.Cancelled }; n++; }
        return n;
    }

    public Task ClosePosition(string symbol, CancellationToken ct = default) => Wait(ct);

    public async Task<List<AssetInfo>> GetAssets(CancellationToken ct = default)
    {
        await Wait(ct);
        return [new("AAPL", "Apple Inc.", "NASDAQ", "us_equity", true, true, true), new("MSFT", "Microsoft Corporation", "NASDAQ", "us_equity", true, true, true),
                new("SPY", "SPDR S&P 500", "ARCA", "us_equity", true, true, true), new("BTC/USD", "Bitcoin", "CRYPTO", "crypto", true, false, true)];
    }

    public async Task<History> GetHistory(string period, string timeframe, CancellationToken ct = default)
    {
        await Wait(ct);
        var n = 30;
        return new History([.. Enumerable.Range(0, n).Select(i => 1_700_000_000L + i * 86_400L)], [.. Enumerable.Range(0, n).Select(i => 990_000_000d + i * 400_000d)]);
    }

    public async Task<List<Bar>> GetBars(string symbol, string timeframe, int limit, CancellationToken ct = default)
    {
        await Wait(ct);
        var t0 = DateTimeOffset.UtcNow.AddDays(-limit).ToUnixTimeMilliseconds();
        return [.. Enumerable.Range(0, limit).Select(i => new Bar(t0 + i * 86_400_000L, Price - 1, Price + 1, Price - 2, Price + (i % 7) - 3, 1000))];
    }
}

/// <summary>One simulated broker account per connected Alpaca key id, like separate Alpaca accounts.</summary>
public sealed class SimFactory(int latencyMs) : IBrokerFactory
{
    readonly ConcurrentDictionary<string, SimBroker> _accounts = new();
    public IBrokerGateway Create(Broker broker, string secret) =>
        secret.StartsWith("sim-", StringComparison.Ordinal) ? _accounts.GetOrAdd(broker.KeyId, _ => new SimBroker(latencyMs)) : throw new BrokerException("Alpaca rejected the API credentials", 401);
}
