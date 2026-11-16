using System.Globalization;
using System.Text.Json;

namespace OpenTrade.Bots;

public sealed record BotOptions(string[] Symbols, bool DryRun = false, decimal Amount = 50, double EveryHours = 24);

public interface IBot
{
    string Name { get; }
    string DefaultSymbols { get; }
    string Description { get; }
    /// <summary>One check of the market. Called by <see cref="BotRunner"/> every interval while the bot is running.</summary>
    Task Step(IOpenTradeClient client, BotSelf self, BotOptions o, Action<string> log, CancellationToken ct);
}

public static class BotRunner
{
    public static void Log(string bot, string msg) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{bot}] {msg}");

    /// <summary>Runs one check. Returns false when the API key is not accepted, so the loop can stop.</summary>
    public static async Task<bool> Tick(IBot bot, IOpenTradeClient client, BotOptions o, Action<string> log, CancellationToken ct)
    {
        try
        {
            var self = await client.GetBot(ct);       // an authenticated call also counts as the heartbeat
            if (!self.Running) log("paused in OpenTrade - waiting");
            else await bot.Step(client, self, o, log, ct);
        }
        catch (BotPausedException) { log("order refused: bot is paused"); }
        catch (ApiException e)
        {
            log("API error: " + e.Message);
            if (e.Status is 401 or 403) { log("The API key is invalid, expired or missing a permission (needs read, positions, trade)."); return false; }
        }
        return true;
    }

    public static async Task Run(IBot bot, IOpenTradeClient client, BotOptions o, TimeSpan interval, bool once, CancellationToken ct)
    {
        void log(string m) => Log(bot.Name, m);
        log($"starting{(o.DryRun ? " (dry run)" : "")}");
        try
        {
            do
            {
                if (!await Tick(bot, client, o, log, ct)) return;
                if (once) return;
                await Task.Delay(interval, ct);
            } while (true);
        }
        catch (OperationCanceledException) { log("stopped"); }
    }

    internal static async Task<HashSet<string>> OpenSymbols(IOpenTradeClient c, CancellationToken ct) =>
        (await c.GetOpenOrders(ct)).Select(x => x.Symbol).ToHashSet();
}

/// <summary>Holds a stock while its 10-day average is above its 30-day average, otherwise stays in cash.</summary>
public sealed class SmaCrossoverBot : IBot
{
    public const int Fast = 10, Slow = 30;
    public string Name => "SMA Crossover";
    public string DefaultSymbols => "AAPL,MSFT,NVDA";
    public string Description => "Holds a stock while its 10-day average is above its 30-day average.";

    public async Task Step(IOpenTradeClient c, BotSelf self, BotOptions o, Action<string> log, CancellationToken ct)
    {
        var budgetEach = self.Allocated / Math.Max(1, o.Symbols.Length);
        var held = await c.GetPositions(ct);
        var busy = await BotRunner.OpenSymbols(c, ct);
        foreach (var sym in o.Symbols)
        {
            if (busy.Contains(sym)) { log($"{sym}: has an open order, skipping"); continue; }
            var closes = (await c.GetBars(sym, "1Day", Slow + 5, ct)).Select(b => b.Close).ToList();
            var trend = Strategies.Crossover(closes, Fast, Slow);
            var have = held.TryGetValue(sym, out var h) ? h.Qty : 0;
            if (trend is null) log($"{sym}: not enough price history");
            else if (trend == Strategies.Trend.Long && have == 0)
            {
                var qty = Strategies.SharesFor(budgetEach, closes[^1]);
                if (qty < 1) log($"{sym}: signal long but the {budgetEach:N0} USD budget is too small for one share");
                else await Place(c, o, log, sym, "buy", qty, ct);
            }
            else if (trend == Strategies.Trend.Flat && have > 0) await Place(c, o, log, sym, "sell", have, ct);
            else log($"{sym}: signal {trend}, holding {have:0.###} - nothing to do");
        }
    }

    internal static async Task Place(IOpenTradeClient c, BotOptions o, Action<string> log, string sym, string side, decimal qty, CancellationToken ct, string prefix = "")
    {
        if (o.DryRun) { log($"{sym}: {prefix}[dry run] would {side.ToUpperInvariant()} {qty:0.###}"); return; }
        var order = await c.PlaceOrder(new OrderTicket(sym, side, qty), ct);
        log($"{sym}: {prefix}{side.ToUpperInvariant()} {qty:0.###} -> order {order.Id} ({order.Status})");
    }
}

/// <summary>Buys a stock when its 14-day RSI drops below 30 (oversold) and sells when it recovers above 55.</summary>
public sealed class RsiMeanReversionBot : IBot
{
    public string Name => "RSI Mean Reversion";
    public string DefaultSymbols => "SPY,QQQ,AAPL";
    public string Description => "Buys oversold stocks (RSI < 30) and sells on recovery.";

    public async Task Step(IOpenTradeClient c, BotSelf self, BotOptions o, Action<string> log, CancellationToken ct)
    {
        var budgetEach = self.Allocated / Math.Max(1, o.Symbols.Length);
        var held = await c.GetPositions(ct);
        var busy = await BotRunner.OpenSymbols(c, ct);
        foreach (var sym in o.Symbols)
        {
            if (busy.Contains(sym)) { log($"{sym}: has an open order, skipping"); continue; }
            var closes = (await c.GetBars(sym, "1Day", 60, ct)).Select(b => b.Close).ToList();
            var rsi = Strategies.Rsi(closes);
            var have = held.TryGetValue(sym, out var h) ? h.Qty : 0;
            var action = Strategies.RsiAction(rsi, have > 0);
            var shown = rsi is { } r ? r.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
            var prefix = $"RSI {shown} - ";
            switch (action)
            {
                case Strategies.Action.Buy:
                    var qty = Strategies.SharesFor(budgetEach, closes[^1]);
                    if (qty < 1) log($"{sym}: RSI {shown} oversold but the budget is too small for one share");
                    else await SmaCrossoverBot.Place(c, o, log, sym, "buy", qty, ct, prefix);
                    break;
                case Strategies.Action.Sell:
                    await SmaCrossoverBot.Place(c, o, log, sym, "sell", have, ct, prefix);
                    break;
                default:
                    log($"{sym}: RSI {shown}, holding {have:0.###} - nothing to do");
                    break;
            }
        }
    }
}

/// <summary>Every N hours buys a fixed USD amount of each symbol (fractional shares), until the bot's allocation is used up.</summary>
public sealed class DcaBot(string? statePath = null) : IBot
{
    readonly string _state = statePath ?? Path.Combine(Directory.GetCurrentDirectory(), ".dca_state.json");
    public string Name => "DCA Index Buyer";
    public string DefaultSymbols => "SPY,QQQ";
    public string Description => "Buys a fixed amount of SPY and QQQ on a schedule.";

    DateTimeOffset LastRun()
    {
        try { return JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(_state)); }
        catch { return DateTimeOffset.MinValue; }
    }

    public async Task Step(IOpenTradeClient c, BotSelf self, BotOptions o, Action<string> log, CancellationToken ct)
    {
        var wait = TimeSpan.FromHours(o.EveryHours) - (DateTimeOffset.UtcNow - LastRun());
        if (wait > TimeSpan.Zero) { log($"next purchase in {wait.TotalHours:0.0} h"); return; }
        var heldValue = (await c.GetPositions(ct)).Values.Sum(p => p.MarketValue);
        foreach (var sym in o.Symbols)
        {
            if (self.Allocated > 0 && heldValue + o.Amount > self.Allocated) { log($"{sym}: allocation of {self.Allocated:N0} USD reached - not buying"); continue; }
            var bars = await c.GetBars(sym, "1Day", 1, ct);
            if (bars.Count == 0) { log($"{sym}: no price available"); continue; }
            var qty = Strategies.SharesFor(o.Amount, bars[^1].Close, fractional: true);
            if (qty <= 0) { log($"{sym}: amount too small"); continue; }
            await SmaCrossoverBot.Place(c, o, log, sym, "buy", qty, ct);
            if (!o.DryRun) heldValue += o.Amount;
        }
        if (!o.DryRun) File.WriteAllText(_state, JsonSerializer.Serialize(DateTimeOffset.UtcNow));
    }
}
