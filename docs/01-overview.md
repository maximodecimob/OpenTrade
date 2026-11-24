# 1. What OpenTrade is (and why I built it this way)

## The idea in one paragraph

OpenTrade is a web app to **manage trading bots**. A trading bot is just a program that looks at prices and decides to buy or sell.
The problem is that if you give every bot the real broker keys, any bot can spend all the money in the account, and you cannot
see what each one is doing. So OpenTrade sits **in the middle**: the broker account (Alpaca, paper trading with fake money) is
connected once, and each bot gets its **own API key** with its own permissions, its **own slice of the money** (a percentage), and a
**pause button**. The web interface shows the dashboard, orders, positions and the profit or loss of each bot.

```
 me, in the browser ──(cookie)──▶  OpenTrade  ──(REST)──▶  Alpaca (paper)
 my bots           ──(API key)─▶  (C# / SQLite)
```

## What you can do with it

| Area | What it does |
|---|---|
| Dashboard / Portfolios | Total equity, profit of the day, the equity curve, cash, how the money is split (per asset class and per bot). |
| Orders | Place market and limit orders, cancel them, export to CSV. Every 15 s the app syncs with the broker, so orders made outside the app appear too. |
| Positions / Assets | Open positions with unrealized P&L and a "close" button; a searchable list of the instruments you can trade. |
| Bots | Create a bot, give it a % of the account with a slider (the total never goes over 100 %), pause / resume it, see its own P&L. |
| API keys | Permissions `read`, `positions`, `trade`. A key can be bound to one bot, then it only sees that bot's orders. Keys expire and can be rotated. |
| Users | `admin` (everything), `trader` (can trade and pause bots), `viewer` (read only). |

There are also three example bots in `Bots/` (SMA crossover, RSI mean reversion and a DCA buyer). They talk to OpenTrade **only through
the public API**, like any external bot would, which was a good way to check that the API is actually usable.

## How to run it

```powershell
dotnet run
```

Then open http://localhost:5080. The first account you create becomes the admin. To see real data, go to **Brokers → Connect broker**
and paste paper keys from alpaca.markets. All the details are in the main [README](../README.md).

## What is in this docs folder

| File | About |
|---|---|
| [01-overview.md](01-overview.md) | This page. |
| [02-architecture.md](02-architecture.md) | How the code is organised, what happens during a request, the data model, the security choices. |
| [03-load-testing.md](03-load-testing.md) | How much traffic the app can take, and what it costs in CPU and RAM. Includes the maximum rate of order operations. |
| [04-specific-interesting-and-hard-things.md](04-specific-interesting-and-hard-things.md) | The parts that were specific, interesting or hard to do, and what I learned from them. |
