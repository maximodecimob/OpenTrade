namespace OpenTrade.Bots;

/// <summary>Pure strategy maths, so it can be tested without a market or a server.</summary>
public static class Strategies
{
    /// <summary>Simple moving average of the last <paramref name="n"/> values, or null when there is not enough data.</summary>
    public static decimal? Sma(IReadOnlyList<decimal> values, int n)
    {
        if (n <= 0 || values.Count < n) return null;
        decimal sum = 0;
        for (var i = values.Count - n; i < values.Count; i++) sum += values[i];
        return sum / n;
    }

    /// <summary>Wilder's RSI. Null when there are fewer than n+1 closes.</summary>
    public static double? Rsi(IReadOnlyList<decimal> closes, int n = 14)
    {
        if (closes.Count < n + 1) return null;
        double gains = 0, losses = 0;
        for (var i = 1; i <= n; i++)
        {
            var d = (double)(closes[i] - closes[i - 1]);
            gains += Math.Max(d, 0);
            losses += Math.Max(-d, 0);
        }
        double avgGain = gains / n, avgLoss = losses / n;
        for (var i = n + 1; i < closes.Count; i++)
        {
            var d = (double)(closes[i] - closes[i - 1]);
            avgGain = (avgGain * (n - 1) + Math.Max(d, 0)) / n;
            avgLoss = (avgLoss * (n - 1) + Math.Max(-d, 0)) / n;
        }
        return avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
    }

    public enum Trend { Long, Flat }

    /// <summary>Long while the fast average is above the slow one, flat below it, null without enough data.</summary>
    public static Trend? Crossover(IReadOnlyList<decimal> closes, int fast = 10, int slow = 30) =>
        Sma(closes, fast) is { } f && Sma(closes, slow) is { } s ? (f > s ? Trend.Long : Trend.Flat) : null;

    public enum Action { Hold, Buy, Sell }

    /// <summary>Mean reversion: buy when oversold and not held, sell when recovered and held.</summary>
    public static Action RsiAction(double? rsi, bool held, double buyBelow = 30, double sellAbove = 55) => rsi switch
    {
        null => Action.Hold,
        < 0 => Action.Hold,
        var v when v < buyBelow && !held => Action.Buy,
        var v when v > sellAbove && held => Action.Sell,
        _ => Action.Hold,
    };

    /// <summary>How many shares fit in the budget. Whole shares unless fractional.</summary>
    public static decimal SharesFor(decimal budget, decimal price, bool fractional = false)
    {
        if (price <= 0 || budget <= 0) return 0;
        return fractional ? Math.Round(budget / price, 3) : Math.Floor(budget / price);
    }
}
