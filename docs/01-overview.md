# 1. Overview

## The idea

OpenTrade is a web app to manage trading bots.
A trading bot is a program that looks at the prices and decides when to buy or sell.
The problem is that if you give the real broker keys to every bot, any bot can spend all the money of the account, and you cannot see what each bot is doing.
So OpenTrade is in the middle: the broker account (Alpaca, paper trading with fake money) is connected only once, and each bot gets its own API key with its own permissions, its own part of the money (a percentage) and a pause button.
The web interface shows the dashboard, the orders, the positions and the profit or loss of each bot.

```
 me, in the browser ──(cookie)──▶  OpenTrade  ──(REST)──▶  Alpaca (paper)
 my bots           ──(API key)─▶  (C# / SQLite)
```

## What you can do with it

| Area | What it does |
|---|---|
| Dashboard / Portfolios | Total equity, profit of the day, the equity curve, cash, and how the money is split (per asset class and per bot). |
| Orders | Place market and limit orders, cancel them and export them to CSV. Every 15 s the app syncs with the broker, so orders made outside the app also appear. |
| Positions / Assets | Open positions with the unrealized P&L and a "close" button, and a list of the instruments you can trade, with search. |
| Bots | Create a bot, give it a % of the account with a slider (the total never goes over 100 %), pause or resume it, and see its own P&L. |
| API keys | Permissions `read`, `positions` and `trade`. A key can be bound to one bot, and then it only sees the orders of that bot. Keys expire and can be rotated. |
| Users | `admin` (everything), `trader` (can trade and pause bots) and `viewer` (read only). |

There are also three example bots in `Bots/` (SMA crossover, RSI mean reversion and a DCA buyer).
They talk to OpenTrade only through the public API, like any external bot would.
This was a good way to check that the API is really usable.

## How to run it

```powershell
dotnet run
```

Then open http://localhost:5080.
The first account you create becomes the admin.
To see real data, go to Brokers → Connect broker and paste paper keys from alpaca.markets.
All the details are in [05-setup-and-usage.md](05-setup-and-usage.md).

## What is in this docs folder

| File | About |
|---|---|
| [01-overview.md](01-overview.md) | This page. |
| [02-architecture.md](02-architecture.md) | How the code is organised, what happens during a request, the data model and the security. |
| [03-load-testing.md](03-load-testing.md) | How much traffic the app can take, and how much CPU and RAM it uses. It also includes the maximum rate of order operations. |
| [05-setup-and-usage.md](05-setup-and-usage.md) | Installation, accounts, broker, example bots, tests, configuration, troubleshooting and limitations. |
| [06-bot-api.md](06-bot-api.md) | The public API for bots: endpoints, orders, capital rules and errors. |
