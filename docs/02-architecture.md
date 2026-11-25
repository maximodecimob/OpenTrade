# 2. Architecture

## The big picture

Everything runs in one process.
The ASP.NET Core app serves the React interface (already built into `wwwroot/`), the internal API for the browser (`/api/...`) and the public API for the bots (`/v1/...`).
The data is in one SQLite file (`data/opentrade.db`).
I chose this on purpose: for a university project, being able to clone the repository and run `dotnet run` was more important for me than a perfect production setup with a separate database server.

```
                       ┌──────────────────────── OpenTrade process ────────────────────────┐
 Browser (React) ─────▶│ security headers + CSRF check ─▶ rate limiter ─▶ auth ─▶ /api/*   │
                       │                                                             │      │
 Bots (API key) ──────▶│                                            /v1/* (Gate) ────┤      │
                       │                                                             ▼      │
                       │   OrderService (checks) ─▶ BrokerHub (cache) ─▶ IBrokerGateway ────┼──▶ Alpaca
                       │          │                                                         │
                       │          ▼                                                         │
                       │   EF Core ─▶ SQLite file          OrderSync (every 15 s) ──────────┼──▶ Alpaca
                       └────────────────────────────────────────────────────────────────────┘
```

| Layer | Technology |
|---|---|
| Backend | C#, ASP.NET Core 10 minimal APIs, EF Core + SQLite, cookie authentication, rate limiting, Data Protection |
| Frontend | React 18, TypeScript, Vite (built into `wwwroot/` and served by the same process) |
| Broker | Alpaca Trading and Market Data REST APIs, behind an `IBrokerGateway` interface |
| Tests | xUnit, `WebApplicationFactory` integration tests with a fake broker |

## Where things are

| File / folder | What is inside |
|---|---|
| `OpenTrade.csproj` | The web app (run `dotnet run` in the root folder). |
| `appsettings*.json` | Configuration (without credentials). |
| `limits.json` | All the rate limits and the maximum number of API keys. |
| `data/` | Database and encryption keys, created on the first start (not in Git). |
| `Api/Program.cs` | Startup: database, cookie login, rate limits, security headers, first admin and a small schema upgrade. |
| `Api/Endpoints.cs` | Everything the web interface calls (`/api`): login, users, dashboard, orders, brokers, bots and keys. |
| `Api/PublicApi.cs` | The bot API (`/v1`). The `Gate` method checks the API key, its permission, its broker and its bot. |
| `Api/Services.cs` | `BrokerHub` (short caches), `OrderService` (validation and capital rules), `OrderSync` and `KeyUsage` (background jobs) and `Ledger` (bot P&L). |
| `Api/Gateway.cs` | The Alpaca client, behind the `IBrokerGateway` interface. |
| `Api/Data.cs` | The EF Core entities: `User`, `Broker`, `Bot`, `Order` and `ApiKey`. |
| `web/` | React + TypeScript + Vite source of the interface. |
| `Bots/` | The three example bots (console app). |
| `Tests/` | xUnit tests (unit tests and integration tests with a fake broker). |
| `LoadTests/` | The load and capacity tests (see [03-load-testing.md](03-load-testing.md)). |

## What happens when a bot places an order

This is the most important flow, so I follow one request from the start to the end (`POST /v1/orders`):

1. Security middleware: it adds the security headers. The `X-OT` CSRF header is only required on `/api`, not on `/v1`.
2. Rate limiter: the `/v1` limit is 120 requests per minute per API key. The bot API does not use the per-IP limit, so many bots on one computer each get their own 120. If the limit is used up, the answer is `429` and nothing else runs.
3. Gate (`PublicApi.cs`): if this IP already sent 30 wrong keys this minute, the answer is `429`. If not, the key is hashed with SHA-256, and the key, its broker and its bot are loaded in one query. A missing, wrong or expired key counts as a wrong key. Then the gate checks if the key has expired and if it has the `trade` permission. The usage stats (last used, request count, heartbeat) are counted in memory and `KeyUsage` writes them for all keys together every 10 s, so a read never waits for a write.
4. Paused: if the bot is paused, the answer is `423`.
5. Idempotency: if the bot sent a `clientOrderId` that already exists, it gets the first order back (`200`) and nothing new is placed.
6. Validation: symbol, side, type, quantity, limit price and time in force.
7. Capital check (`OrderService.CheckBotLimits`), with a lock per bot so two orders of the same bot cannot both pass:
   - a sell cannot be bigger than what the bot holds (no short selling);
   - a buy must fit: (value of the bot's positions + its open buy orders + this order) ≤ its % of the account equity.
8. Broker: the order is sent to Alpaca.
9. Database: the order is saved with the bot it belongs to. If the background sync already inserted the same broker order (a race condition), the unique index catches it, and the code keeps that row and only adds the bot.
10. The cache of that account is cleared, so the next dashboard shows the new state.

## Data model

```
User           Broker ──< Bot ──< Order
                  │        │
                  └──< ApiKey (optionally bound to one Bot)
```

- A Broker is one connected Alpaca account. Its secret is encrypted with ASP.NET Data Protection (and on Windows the key ring is also encrypted with DPAPI).
- A Bot has `AllocPct`, a percentage of its broker account. The USD amount is calculated every time from the current equity and it is never stored, so it follows the account when the account grows or falls.
- An Order keeps both ids: our own (`OT-...`, or the `clientOrderId` of the bot) and the Alpaca one (`BrokerOrderId`, unique).
- An ApiKey stores only the SHA-256 hash of the secret, never the secret itself. The secret is shown only once, when the key is created.
- Users are not linked to brokers. Every user sees every broker account. I explain why this matters in the load testing doc.

## Caching

The broker is slow compared to the app (a round trip to Alpaca takes tens of milliseconds), so `BrokerHub` caches the read calls: account and positions for 5 s, price bars for 15 s, equity history for 60 s and the asset list for 1 h.
The trick is that it caches the running task, not the result.
So if 50 requests ask for the same account at the same moment, only one call goes to Alpaca and the 50 requests wait for it.
If a call fails, it is removed from the cache at once, so an error is not cached for 5 seconds.

I use the same trick for the dashboard and bots pages, which are the same for every user: the computed page is shared for 2 seconds, and any change made in the web UI clears it at once.

## Database writes

SQLite allows only one writer at a time.
The database runs in WAL mode (readers do not wait for the writer), and every `SaveChanges` goes through one async queue (`OtDb.WriteGate`), so two writes never collide inside SQLite.

## Security

- There are no credentials in the repository. The first account created becomes the admin (or the admin comes from the `Admin__*` environment variables). Self-registration creates read-only accounts and it can be turned off.
- Passwords are hashed with PBKDF2-SHA256 with 210,000 iterations. A wrong e-mail also uses the same CPU time, so you cannot find out if an e-mail exists by measuring the time.
- Broker secrets are encrypted with ASP.NET Data Protection, and API keys are stored only as SHA-256 hashes.
- The session cookie is HttpOnly and SameSite=Strict, and it is checked against the database on every request, so if an admin disables a user or changes their role, it applies immediately.
- CSRF: every write from the browser needs the `X-OT: 1` header. A browser cannot add custom headers to a cross-site request without a CORS preflight, and the app never accepts one.
- Rate limits: login 5/min per IP, bot API 120/min per key, wrong API keys 30/min per IP and web interface 600/min per IP. Behind a reverse proxy, `X-Forwarded-For` is only accepted from the local machine.
- Strict Content-Security-Policy and security headers, role-based authorization, input validation and CSV export protected against formula injection.
- Live trading (with real money) is disabled unless `Brokers:AllowLive=true` is set.

## Configuration I added for the load tests

These settings exist so the load tests can go over the limits.
All of them are together in `limits.json`, in the root folder.
The defaults are the same as before, so normal use does not change.

| Setting | Default | Meaning |
|---|---|---|
| `RateLimit:V1PerMinute` | 120 | Requests per minute per API key on `/v1`. |
| `RateLimit:GlobalPerMinute` | 600 | Requests per minute per IP, for the web interface (not for the bot API). |
| `RateLimit:BadKeyPerMinute` | 30 | Requests with a missing, wrong or expired API key, per minute per IP. |
| `RateLimit:LoginPerMinute` | 5 | Login and register attempts per minute per IP (this one already existed). |
| `Keys:MaxCount` | 50 | Maximum number of API keys in the whole app (so at most 50 bots can trade). |
