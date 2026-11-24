# OpenTrade Manager

A web application for managing algorithmic trading. It connects to a broker (**Alpaca**, paper trading), stores its own data in a
database, and offers a **REST API** that external trading bots use to place orders, with per-bot permissions, capital allocation
and a pause switch.

University project — C# / ASP.NET Core.

| | |
|---|---|
| **Author** | Jacobo Bascón Montalvo |

---

## Quick start

**Requirement:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Nothing else is needed: the web interface
is already built and included.

```bash
git clone <repository-url>
cd OpenTrade
dotnet run
```

Open **http://localhost:5080**. There are **no default credentials**: on a fresh install the page shows **Create account**, and the
**first account you create becomes the administrator**. Every later account is a read-only `viewer` (an admin can change the role in
Settings → Users).

### Creating an account (for the person who grades or reviews it)

Either use the **Create account** button on the sign-in page, or create the account from a terminal while the app is running.
The password needs at least 12 characters.

PowerShell (Windows):

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5080/api/register `
  -Headers @{ "X-OT" = "1" } -ContentType "application/json" `
  -Body '{"email":"professor@example.com","name":"Professor","password":"a-long-password-1"}'
```

curl (Linux / macOS / Git Bash):

```bash
curl -X POST http://localhost:5080/api/register \
  -H "Content-Type: application/json" -H "X-OT: 1" \
  -d '{"email":"professor@example.com","name":"Professor","password":"a-long-password-1"}'
```

The answer is the new user (`201`) with its role. Then sign in on the web page with that email and password.

> A self-registered account is a **viewer**: it can see everything but cannot trade or change settings, because it shares the broker
> data of the administrator. To give it more rights, sign in as admin → **Settings → Users** → change the role. To turn
> self-registration off, start the app with `Registration__Enabled=false`; to choose the role of new accounts (`viewer` or `trader`),
> use `Registration__Role=trader`.

**Setting the administrator from the command line instead** (for example on a server, or to reset a fresh database): stop the app,
delete the `data/` folder and start it with the admin in the environment. It is only used when no user exists yet:

```powershell
$env:Admin__Email = "me@example.com"; $env:Admin__Password = "a-long-password-1"; dotnet run
```

> To start again from zero, stop the app and delete the `data/` folder.

### Connect a broker (to see real data)

Without a broker the app starts empty. To trade with fake money:

1. Create a free account at [alpaca.markets](https://alpaca.markets) and open **Paper Trading → API Keys → Generate**.
2. In OpenTrade go to **Brokers → Connect broker** and paste the *Key ID* and *Secret*.

The keys are checked against Alpaca and then stored encrypted. Dashboard, portfolios, positions, orders and assets now show your paper account.

---

## Features

- **Dashboard and portfolios**: equity, daily P&L, equity curve, cash, allocation per asset class and per bot.
- **Orders**: place market and limit orders, with the latest price and the estimated cost shown before you confirm. You can also cancel orders, cancel everything open, and export to CSV.
  Orders are synced from the broker every 15 s, so orders made outside the app show up too.
- **Positions and assets**: open positions with unrealized P&L and a one-click close, plus a searchable list of tradable instruments.
- **Bots**: register a bot and give it a **percentage of the broker account** with a slider. The percentages of one account never add up to more than 100%, and a bot can never spend more than its share (see *Capital rules*). Follow its positions, realized and unrealized P&L and heartbeat, and pause or resume it from the UI.
- **API keys**: per-key permissions (`read`, `positions`, `trade`), optionally bound to a single bot. Each key has an expiry and can be rotated or revoked.
- **Users and roles**:
  - `admin`: everything.
  - `trader`: trading and pausing bots.
  - `viewer`: read-only.

  Disabling a user signs them out immediately.
- **Example bots** written in C# (see below).

## Example bots

`Bots/` contains three strategies that connect to OpenTrade only through the public API, as any external bot would:

| Bot | Strategy |
|---|---|
| SMA Crossover | Holds AAPL, MSFT and NVDA while the 10-day moving average is above the 30-day one, otherwise stays in cash. |
| RSI Mean Reversion | Buys SPY, QQQ and AAPL when the 14-day RSI drops below 30, sells when it rises above 55. |
| DCA Index Buyer | Buys 50 USD of SPY and QQQ every 24 hours until its allocation is used. |

With OpenTrade running and a broker connected, run these from the repository root:

```bash
dotnet run --project Bots -- setup                      # creates the 3 bots (10% of the account each) and their API keys; asks for the admin login
dotnet run --project Bots -- run all --dry-run --once   # shows what they would trade, places nothing
dotnet run --project Bots -- run all                    # runs them (Ctrl+C to stop)
```

A single bot is run with `run sma`, `run rsi` or `run dca`. Run `dotnet run --project Bots -- help` to see all options.

## Architecture

```
 Browser (React SPA)  ──cookie session──▶ ┌────────────────────────────┐ ──REST──▶ Alpaca (paper)
                                          │  OpenTrade API (ASP.NET)   │
 External bots        ──API key (/v1)───▶ │  EF Core ─▶ SQLite         │ ◀─sync── order status every 15 s
                                          └────────────────────────────┘
```

| Layer | Technology |
|---|---|
| Backend | C#, ASP.NET Core 10 minimal APIs, EF Core + SQLite, cookie authentication, rate limiting, Data Protection |
| Frontend | React 18, TypeScript, Vite (built into `wwwroot/`, served by the same process) |
| Broker | Alpaca Trading and Market Data REST APIs, behind an `IBrokerGateway` interface |
| Tests | xUnit, `WebApplicationFactory` integration tests with a fake broker |

### Project structure

```
OpenTrade.csproj  the web app (run `dotnet run` in the root folder)
appsettings*.json configuration (no credentials)
wwwroot/          built web interface
data/             database and encryption keys, created on first start (not in Git)
Api/              C# source of the web app
  Program.cs      startup, security, first admin, small schema upgrades
  Endpoints.cs    endpoints used by the web interface (/api)
  PublicApi.cs    endpoints for bots (/v1, API-key authentication)
  Gateway.cs      Alpaca client (IBrokerGateway)
  Services.cs     caching, order placement and capital checks, background sync, bot P&L ledger
  Data.cs         database entities (EF Core)
web/            React source of the web interface
Bots/           example trading bots (console app)
Tests/          unit and integration tests
LoadTests/      load and capacity tests (console app, see LoadTests/README.md)
docs/           documentation (overview, architecture, load testing, hard things)
```

## Bot API

The complete, always up to date documentation with examples (curl, Python, C#) is in the app under **API documentation**. Summary:

Each request sends a key created in **API Management**: `Authorization: Bearer ot_test_...` (or `X-API-Key`).

| Method | Path | Permission | Description |
|---|---|---|---|
| GET | `/v1/account` | read | balance of the broker account (and the bot's allocation, for a bot-bound key) |
| GET | `/v1/bot` · POST `/v1/heartbeat` | read | bot state: running, `allocated` (USD) and `allocationPct` |
| GET | `/v1/bars/{symbol}?timeframe=1Day&limit=100` | read | price history, oldest first |
| GET | `/v1/positions` | positions | positions (only the bot's own if the key is bound to a bot) |
| GET | `/v1/orders?status=open&limit=50`, `/v1/orders/{id}` | read | orders |
| POST | `/v1/orders` | trade | place an order |
| DELETE | `/v1/orders/{id}` | trade | cancel an open order |

Place an order:

```bash
curl -X POST http://localhost:5080/v1/orders \
  -H "Authorization: Bearer ot_test_..." -H "Content-Type: application/json" \
  -d '{"symbol":"AAPL","side":"buy","type":"limit","qty":10,"limitPrice":225.5,"timeInForce":"day","clientOrderId":"sma-AAPL-2026-11-17"}'
```

| Field | Required | Meaning |
|---|---|---|
| `symbol` | yes | ticker (`AAPL`) or crypto pair (`BTC/USD`) |
| `side` | yes | `buy` or `sell` |
| `type` | no | `market` (default) or `limit` |
| `qty` | yes | quantity, greater than 0 |
| `limitPrice` | limit orders | price limit |
| `timeInForce` | no | `day` (default) or `gtc` |
| `clientOrderId` | no | your own unique id (8-48 chars: letters, digits, `-`, `_`) |

**Idempotency.** Sending the same `clientOrderId` again (for example after a timeout) returns `200` with the original order and places
nothing new. A first request returns `201`.

**Capital rules** (bot-bound keys). A bot can only spend its allocation: a buy is refused with `409` if the bot's positions plus its
open buy orders would be worth more than `allocationPct` of the account (market orders are priced at the latest quote), and a bot with
0% cannot buy at all. A sell larger than what the bot holds is refused too (no shorting). The check runs before the order reaches the
broker, one order at a time per bot.

**Errors** are always `{ "error": "message" }`: `400` invalid input, `401` missing/invalid/expired key, `403` missing permission, `404`
not found, `409` refused by a rule (capital, holdings, broker rejection, duplicate id), `423` bot paused, `429` more than 120
requests per minute per key, `502` broker unreachable.

## Security

- No credentials are stored in the repository: the first account created becomes the admin (or the admin comes from `Admin__*` environment variables). Self-registration creates read-only accounts and can be switched off.
- Passwords are hashed with PBKDF2-SHA256 (210,000 iterations). Broker secrets are encrypted with ASP.NET Data Protection, and API keys are stored only as SHA-256 hashes and shown once.
- The session cookie is HttpOnly and SameSite=Strict. It is re-validated on every request, so disabled users and role changes apply immediately.
- Every write from the browser must carry an `X-OT` header (CSRF protection). Login and account creation are rate-limited to 5 attempts per minute per IP, the bot API to 120 requests per minute per key (each bot has its own limit, even when many bots run on one computer), requests with a wrong API key to 30 per minute per IP, and the web interface to 600 requests per minute per IP. Behind a reverse proxy, `X-Forwarded-For` is honoured only from the local machine.
- Strict Content-Security-Policy and security headers, role-based authorization, input validation, and CSV export protected against formula injection.
- Live (real-money) trading is **disabled** unless `Brokers:AllowLive=true` is set.

## Tests

```bash
dotnet test OpenTrade.slnx
```

There are more than 90 tests:

- strategy maths, the P&L ledger, order validation, password hashing, and Alpaca JSON parsing (against recorded responses);
- every API endpoint, roles, API-key scoping, account creation, capital limits (percentages, spending and selling rules) and idempotent orders, using a fake broker so no Alpaca account is needed;
- the three bots running end-to-end against the real API.

## Documentation and load tests

The `docs/` folder explains the app in more depth: [overview](docs/01-overview.md), [architecture](docs/02-architecture.md),
[load testing](docs/03-load-testing.md) and [specific, interesting and hard things](docs/04-specific-interesting-and-hard-things.md).

To see the maximum traffic the server handles (about 70 seconds, see [LoadTests/README.md](LoadTests/README.md)):

```bash
dotnet build LoadTests -c Release
dotnet LoadTests/bin/Release/net10.0/OpenTrade.LoadTests.dll max
```

On my laptop (16 cores) it sustains about **1,500 bots (150 users × 10 bots), about 2,500 requests/s and 700 order operations/s**.
The rate limits are configurable: `RateLimit:V1PerMinute` (120 per bot key), `RateLimit:GlobalPerMinute` (600 per IP, web interface),
`RateLimit:BadKeyPerMinute` (30 wrong keys per IP), `RateLimit:LoginPerMinute` (5), and `Keys:MaxCount` (50 API keys).

## Changing the web interface

Requires Node.js 18+.

```bash
cd web
npm install
npm run dev       # http://localhost:5173, forwards /api to the backend on :5080
npm run build     # writes to ../wwwroot
```

> Vite fails if the folder path contains `#` (for example `C:\...\C#\OpenTrade`). Use a path without `#` to build the frontend.

## Troubleshooting

| Problem | Solution |
|---|---|
| "Invalid email or password" | The account does not exist or the password is wrong. Create a new one with **Create account**, or stop the app, delete `data/` and start again to make a new first (admin) account. |
| Port 5080 already in use | Another copy is still running. Close it, or run `dotnet run -- --urls http://localhost:5090`. |
| Build error "file is being used by another process" | Stop the running app (Ctrl+C) before building or testing. |
| Broker shows an error | The Alpaca keys were regenerated or are paper/live mismatched. Use **Brokers → Update keys**. |

## Limitations

- The database schema is created at startup (`EnsureCreated`), without migrations. A small upgrade step at startup moves older files to the percentage-based allocation (old fixed USD amounts restart at 0%, so set the sliders again).
- A bot's P&L is calculated from its own fills with the average-cost method. Bots cannot sell short (the API refuses a sell larger than the bot's holdings).
- The app speaks plain HTTP on localhost. For anything public, put it behind an HTTPS reverse proxy.
- Market data comes from Alpaca's free IEX feed and can be slightly delayed.
