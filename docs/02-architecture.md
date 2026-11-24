# 2. Architecture

## The big picture

It is **one process**. The ASP.NET Core app serves the React interface (already built into `wwwroot/`), the internal API for the browser
(`/api/...`) and the public API for bots (`/v1/...`). The data is in one SQLite file (`data/opentrade.db`). I chose this on purpose: for a
university project, "clone and `dotnet run`" was more important to me than a perfect production setup with a separate database server.

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

## Where things are

| File | What is inside |
|---|---|
| `Api/Program.cs` | Startup: database, cookie login, rate limits, security headers, first admin, a small schema upgrade. |
| `Api/Endpoints.cs` | Everything the web interface calls (`/api`): login, users, dashboard, orders, brokers, bots, keys. |
| `Api/PublicApi.cs` | The bot API (`/v1`). The `Gate` method checks the API key, its permission, its broker and its bot. |
| `Api/Services.cs` | `BrokerHub` (short caches), `OrderService` (validation + capital rules), `OrderSync` and `KeyUsage` (background jobs), `Ledger` (bot P&L). |
| `Api/Gateway.cs` | The Alpaca client, behind the `IBrokerGateway` interface. |
| `Api/Data.cs` | The EF Core entities: `User`, `Broker`, `Bot`, `Order`, `ApiKey`. |
| `web/` | React + TypeScript + Vite source of the interface. |
| `Bots/` | The three example bots (console app). |
| `Tests/` | xUnit tests (unit + integration with a fake broker). |
| `LoadTests/` | The load and capacity tests (see [03-load-testing.md](03-load-testing.md)). |

## What happens when a bot places an order

This is the most important flow, so I follow one request from start to end (`POST /v1/orders`):

1. **Security middleware**: adds the security headers. (The `X-OT` CSRF header is only required on `/api`, not on `/v1`.)
2. **Rate limiter**: the `/v1` limit, 120 requests per minute **per API key**. The bot API does not use the per-IP limit, so many bots
   on one computer each get their own 120. If it is used up, the answer is `429` and nothing else runs.
3. **Gate** (`PublicApi.cs`): if this IP already sent 30 wrong keys this minute, the answer is `429`. Otherwise the key is hashed with
   SHA-256, and the key, its broker and its bot are loaded in **one** query. A missing, wrong or expired key counts as a wrong key. Then come
   the checks: has it expired? does it have the `trade` permission? Usage stats (last used, request count, heartbeat) are counted in
   memory and written for all keys together every 10 s by `KeyUsage`, so a read never waits for a write.
4. **Paused?** If the bot is paused, the answer is `423`.
5. **Idempotency**: if the bot sent a `clientOrderId` that already exists, it gets the first order back (`200`) and nothing new is placed.
6. **Validation**: symbol, side, type, quantity, limit price, time in force.
7. **Capital check** (`OrderService.CheckBotLimits`), under a **lock per bot** so two orders of the same bot cannot both pass:
   - a sell must not be bigger than what the bot holds (no short selling);
   - a buy must fit: (value of the bot's positions + its open buy orders + this order) ≤ its % of the account equity.
8. **Broker**: the order is sent to Alpaca.
9. **Database**: the order is saved, with the bot it belongs to. If the background sync already inserted the same broker order
   (a race), the unique index catches it and the code keeps that row and just adds the bot.
10. The cache for that account is cleared, so the next dashboard shows the new state.

## Data model

```
User           Broker ──< Bot ──< Order
                  │        │
                  └──< ApiKey (optionally bound to one Bot)
```

- A **Broker** is one connected Alpaca account. Its secret is encrypted with ASP.NET Data Protection (and on Windows the key ring is also
  encrypted with DPAPI).
- A **Bot** has `AllocPct`, a percentage of its broker account. The USD amount is calculated each time from the current equity and is
  never stored, so it follows the account when the account grows or falls.
- An **Order** keeps both ids: our own (`OT-...`, or the bot's `clientOrderId`) and Alpaca's (`BrokerOrderId`, unique).
- An **ApiKey** stores only the SHA-256 hash of the secret, never the secret itself. The secret is shown once when it is created.
- **Users are not linked to brokers.** Every user sees every broker account. I explain why this matters in the load test doc.

## Caching

The broker is slow compared to the app (a round trip to Alpaca is tens of milliseconds), so `BrokerHub` caches the read calls: account
and positions 5 s, price bars 15 s, equity history 60 s, the asset list 1 h. The trick is that it caches the **running task**, not the
result. So if 50 requests ask for the same account at the same moment, only **one** call goes to Alpaca and all 50 wait for it.
A call that fails is removed from the cache at once, so an error is not cached for 5 seconds.

The same trick is used for the **dashboard** and **bots** pages, which are the same for every user: the computed page is shared for
2 seconds, and any change made in the web UI clears it at once.

## Database writes

SQLite allows one writer at a time. The database runs in **WAL mode** (readers do not wait for the writer), and every `SaveChanges`
goes through one async queue (`OtDb.WriteGate`), so two writes never collide inside SQLite. Why this matters is explained in
[04-specific-interesting-and-hard-things.md](04-specific-interesting-and-hard-things.md).

## Security choices (short)

- Passwords: PBKDF2-SHA256 with 210,000 iterations. A wrong e-mail also burns the same CPU time, so you cannot find out if an e-mail exists by measuring the time.
- Session cookie: HttpOnly, SameSite=Strict, and re-checked against the database on **every** request, so if an admin disables a user, that user is out immediately.
- CSRF: every write from the browser needs the `X-OT: 1` header. A browser cannot add custom headers to a cross-site request without a CORS preflight, and the app never approves one.
- Rate limits: login 5/min per IP, bot API 120/min per key, wrong API keys 30/min per IP, web interface 600/min per IP.
- Strict Content-Security-Policy, CSV export protected against formula injection, live trading off unless `Brokers:AllowLive=true`.

## Configuration I added for the load tests

These settings exist so the load tests can go past the limits. The defaults are the same as before, so normal use does not change.

| Setting | Default | Meaning |
|---|---|---|
| `RateLimit:V1PerMinute` | 120 | Requests per minute per API key on `/v1`. |
| `RateLimit:GlobalPerMinute` | 600 | Requests per minute per IP, for the web interface (not for the bot API). |
| `RateLimit:BadKeyPerMinute` | 30 | Requests with a missing, wrong or expired API key, per minute per IP. |
| `RateLimit:LoginPerMinute` | 5 | Login and register attempts per minute per IP (this one already existed). |
| `Keys:MaxCount` | 50 | Maximum number of API keys in the whole app (so at most 50 bots can trade). |
