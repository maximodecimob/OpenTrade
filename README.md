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

Open **http://localhost:5080** and sign in:

| Email | Password |
|---|---|
| `admin@admin.com` | `OpenTrade#2026` |

On the first start the app creates the database (`data/opentrade.db`) and this admin account, and prints both in the console.
You can change the password afterwards in **Settings**.

> These credentials come from `appsettings.Development.json` and are only used the first time, when no user exists yet.
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
- **Bots**: register a bot with an amount of capital, then follow its positions, realized and unrealized P&L and heartbeat. Pause or resume it from the UI.
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
dotnet run --project Bots -- setup                      # creates the 3 bots and their API keys (asks for the admin login)
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
appsettings*.json configuration (the default admin is in appsettings.Development.json)
wwwroot/          built web interface
data/             database and encryption keys, created on first start (not in Git)
Api/              C# source of the web app
  Program.cs      startup, security, first admin
  Endpoints.cs    endpoints used by the web interface (/api)
  PublicApi.cs    endpoints for bots (/v1, API-key authentication)
  Gateway.cs      Alpaca client (IBrokerGateway)
  Services.cs     caching, order placement, background sync, bot P&L ledger
  Data.cs         database entities (EF Core)
web/            React source of the web interface
Bots/           example trading bots (console app)
Tests/          unit and integration tests
```

## Bot API (summary)

Each request sends a key created in **API Management**: `Authorization: Bearer ot_test_...`

| Method | Path | Permission | Description |
|---|---|---|---|
| GET | `/v1/account` | read | balance of the broker account |
| GET | `/v1/bot` · POST `/v1/heartbeat` | read | bot state (running, allocation) |
| GET | `/v1/bars/{symbol}?timeframe=1Day&limit=100` | read | price history |
| GET | `/v1/positions` | positions | positions (only the bot's own if the key is bound to a bot) |
| GET | `/v1/orders`, `/v1/orders/{id}` | read | orders |
| POST | `/v1/orders` | trade | place an order: `{"symbol":"AAPL","side":"buy","type":"market","qty":1}` |
| DELETE | `/v1/orders/{id}` | trade | cancel an order |

A paused bot gets `423`. The limit is 120 requests per minute per key. The full documentation is in the app under **API documentation**.

## Security

- Passwords are hashed with PBKDF2-SHA256 (210,000 iterations). Broker secrets are encrypted with ASP.NET Data Protection, and API keys are stored only as SHA-256 hashes and shown once.
- The session cookie is HttpOnly and SameSite=Strict. It is re-validated on every request, so disabled users and role changes apply immediately.
- Every write from the browser must carry an `X-OT` header (CSRF protection). Login is rate-limited to 5 attempts per minute, and the bot API to 120 requests per minute per key.
- Strict Content-Security-Policy and security headers, role-based authorization, input validation, and CSV export protected against formula injection.
- Live (real-money) trading is **disabled** unless `Brokers:AllowLive=true` is set.

## Tests

```bash
dotnet test OpenTrade.slnx
```

There are 80 tests:

- strategy maths, the P&L ledger, order validation, password hashing, and Alpaca JSON parsing (against recorded responses);
- every API endpoint, roles, and API-key scoping, using a fake broker so no Alpaca account is needed;
- the three bots running end-to-end against the real API.

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
| "Invalid email or password" with the default credentials | The account already exists with another password, or an `Admin__Password` environment variable overrides it. Stop the app, delete `data/`, open a new terminal and run `dotnet run` again. |
| Port 5080 already in use | Another copy is still running. Close it, or run `dotnet run -- --urls http://localhost:5090`. |
| Build error "file is being used by another process" | Stop the running app (Ctrl+C) before building or testing. |
| Broker shows an error | The Alpaca keys were regenerated or are paper/live mismatched. Use **Brokers → Update keys**. |

## Limitations

- The database schema is created at startup (`EnsureCreated`), without migrations.
- A bot's P&L is calculated from its own fills with the average-cost method. Short positions are not tracked per bot.
- Market data comes from Alpaca's free IEX feed and can be slightly delayed.
