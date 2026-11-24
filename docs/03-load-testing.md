# 3. Load testing: how much traffic can OpenTrade take?

I wanted to answer four questions:

1. What is the **maximum traffic the configuration allows** (the rate limits), and do the limits really work?
2. What is the **maximum traffic the server can handle**, and what does it **consume** (CPU, RAM) at that point?
3. What is the **maximum rate of order operations** (place / cancel)?
4. What breaks first, and can it be fixed?

All tests were run on my laptop: 16 logical cores, 31 GB RAM, Windows 11, .NET 10, Release build.

## The 1-minute demo

```powershell
dotnet build LoadTests -c Release
dotnet LoadTests/bin/Release/net10.0/OpenTrade.LoadTests.dll max
```

It takes about **70 seconds**. It adds users in 10-second steps (10, 50, 100, 150, 200, 300), each with **10 bots** and one open
browser tab, and stops at the first step where the server cannot keep up. Then it does one **burst** with no pauses at all. Other
sizes: `max --users 100 --bots 10` (only "100 users × 10 bots"), `--seconds 20` for longer steps.

Result of the last run (after the optimizations described below):

```
  Users × bots         req/s (wanted)    p95 ms   orders/s   CPU avg/peak   RAM
  10 users × 10 bots      176 (  172)         81        50      8% /  39%      166 MB  OK
  50 users × 10 bots      852 (  858)         82       231     20% /  39%      236 MB  OK
  100 users × 10 bots    1703 ( 1717)        155       469     25% /  52%      301 MB  OK
  150 users × 10 bots    2535 ( 2575)        952       685     31% /  70%      458 MB  OK
  200 users × 10 bots    2737 ( 3433)       1770       822     33% /  56%      596 MB  too slow
  MAXIMUM SUSTAINED : 150 users × 10 bots -> 2535 requests/s (152102 per minute), p95 952 ms
                      of which 685 order operations/s (place + cancel), CPU 31% avg, RAM 458 MB
  MAXIMUM BURST     : 5300 answered requests/s (all 100% OK), p95 66 ms, CPU 59% avg / 74% peak, RAM 521 MB
```

**How to read it.** "Wanted" is what the simulated users try to send. A step is **OK** when the server answers at least 90 % of it,
the p95 latency is under 1 second and there are no errors. So the answer is: **about 1,500 bots (150 users × 10 bots), about 2,500
requests per second (150,000 per minute), of which about 700 are order operations per second.** At 2,000 bots the server still answers
everything (no errors), but it falls behind and the slowest 5 % of requests wait almost 2 seconds.

## How the test works (short)

The full explanation is in [LoadTests/README.md](../LoadTests/README.md). The important points:

- It is the **real app** (same `Program.cs`, middleware, SQLite file, rate limiter) on a **real socket**, started in a **separate process**, so
  the CPU and RAM numbers are the server's only. The server prints its own CPU / memory / GC / threads every 250 ms.
- **Alpaca is simulated** (`SimBroker`, 30 ms per call, like a real network round trip). Alpaca's own limit is about 200 requests per
  minute, so testing against it would only measure Alpaca, and it would fill my paper account with thousands of fake orders.
- **Each simulated user has their own IP** (`X-Forwarded-For`, which the app trusts from localhost, where a reverse proxy would be), so
  the per-IP limit behaves like in production.
- **What a bot does**: 100 requests per minute (just under the 120/min limit), in a loop of 6 calls: bot status, price bars, positions,
  open orders, **place** a limit order, **cancel** it. That is much busier than the example bots in `Bots/`, which check the market
  every few minutes. **What a browser tab does**: what the React app polls: dashboard every 10 s, orders and bots pages every 5 s.
- For the `max` demo, the rate limits are raised, so we measure the server and not the limiter (the bots still send only 100 requests/min
  each, and the burst at the end has no pauses). The limits themselves are tested separately (next section).

Other scenarios (longer, about 15 minutes all together): `limits`, `fleet`, `ceiling`, `orders`, or `all`.

## 1. Maximum traffic the configuration allows

With the default settings, these are hard limits. The `limits` scenario checks that each one gives exactly the expected number:

| Test | Sent | Got through | Expected | Why |
|---|---:|---:|---:|---|
| A. one API key, one IP | 200 | **120** | 120 | `RateLimit:V1PerMinute` = 120 per key |
| B. 7 keys from the same IP | 900 | **840** | 840 | the bot API is limited per key only, so each bot gets its own 120 |
| C. 7 keys from 7 IPs | 1050 | **840** | 840 | the same: the IP does not matter for bots |
| F. made-up API keys, one IP | 40 | 30 × `401`, then `429` | 30 | `RateLimit:BadKeyPerMinute` = 30 per IP |
| D. wrong password, one IP | 12 | 5 × `401`, then `429` | 5 | `RateLimit:LoginPerMinute` = 5 |
| E. one key at the edge of the minute | 120 + 150 | **240 in ≈ 2.5 s** | (120/min?) | fixed window: the counter resets at the edge |

So the maximum traffic **allowed** by the default configuration is:

- **per bot**: 120 requests/min (2 per second), no matter how many bots run on the same computer;
- **per computer (IP), web interface only**: 600 requests/min;
- **wrong API keys**: 30 per minute per IP;
- **for the whole server**: at most **50 API keys** (`Keys:MaxCount`), so at most 50 bots × 120 = **6,000 bot requests per minute
  (100 per second)**, plus the web users.

That is about **4 % of what the server can actually handle** (2,500 req/s above). The limits are there for safety (a bot stuck in a loop,
someone guessing passwords or keys), not because the server needs them. All of them are settings, so they can be changed without code.

At first the per-IP limit (600/min) was used for the bot API too, so 7 bots on one computer only got 600 together (test B gave 600) and
one computer could run only 5 bots at full speed. Now bots are only limited per key (see
[04-specific-interesting-and-hard-things.md](04-specific-interesting-and-hard-things.md)).

## 2. Maximum traffic and consumption

### Raw ceiling of single endpoints

The `ceiling` scenario turns the limits off and sends requests back to back from more and more parallel connections (8 s each).
Numbers from the run before the optimizations; reads were already fast, so they changed little:

| Endpoint | Best req/s | p95 at best | Server CPU | Server RAM |
|---|---:|---:|---:|---:|
| `GET /v1/bot` (API key check + database) | ≈ 8,200 | 3 ms | 36 % | 145 MB |
| `GET /v1/bars` (cached prices) | ≈ 8,100 | 7 ms | 38 % | 157 MB |
| `GET /api/orders` page (cookie session) | ≈ 4,700 | 131 ms | 40 % | 172 MB |
| `GET /api/dashboard` (cookie session) | ≈ 4,100 | 49 ms | 46 % | 175 MB |
| `POST /api/login` (PBKDF2) | ≈ 420 | 224 ms | **92 %** | 165 MB |

The load generator runs on the same laptop, so on a dedicated server the ceiling would be higher. Two things stood out:
- **Reads never used all the CPU** (about 40 %). The limit is the per-request work (SQLite queries, JSON), not raw CPU.
- **Login is the only CPU-bound endpoint.** PBKDF2 with 210,000 iterations costs about 27 ms of CPU per login. 64 logins in parallel use
  92 % of all 16 cores. This is why the login limit (5/min per IP) also protects the server, not only the passwords.

### Memory

| Situation | Server RAM (working set) |
|---|---:|
| Just started, idle | ≈ 105–145 MB |
| 1,000 bots + 100 tabs | ≈ 260–300 MB |
| 1,500 bots + 150 tabs (the maximum) | ≈ 460 MB |
| 2,000 bots (overloaded) | ≈ 600 MB |

Memory grows with the number of requests **in flight** (each waiting request holds its buffers, its EF context and its task), not with
the data. The GC heap itself stayed small (under 300 MB). When the server falls behind, requests pile up and memory goes up quickly.
With only the first four optimizations (before the shared pages and the batched key usage), 2,000 bots took the server to almost 2 GB.

## 3. Maximum rate of order operations

From the `orders` scenario (limits off, simulated broker with 0 ms or 40 ms latency, market buys of 1 share). "Before" is the full run
(8 s per step); "after" is a `--quick` run (3 s per step), so the after numbers are a bit less stable:

| Situation | Before | After | Why it is limited |
|---|---:|---:|---|
| **1 bot**, 1 connection (0 ms broker) | 132 /s | 146 /s | one order after the other |
| **1 bot**, 16 parallel connections (0 ms) | 198 /s | 176 /s | the **per-bot lock**: parallel requests of one bot wait for each other |
| **1 bot**, 16 parallel (40 ms broker) | 10 /s | 11 /s | one bot = at most 1 order per broker round trip (2 calls × 40 ms + work ≈ 95 ms) |
| **64 bots** in parallel (0 ms) | 232 /s (p95 594 ms) | **1,374 /s (p95 72 ms)** | SQLite writes, one at a time |
| **256 bots** in parallel (40 ms broker) | 122 /s (p95 3.4 s) | **1,589 /s (p95 228 ms)** | SQLite writes |
| web UI orders, 16 parallel (no bot, no capital check) | 326 /s | 1,559 /s | SQLite writes |
| cancel orders, 64 bots in parallel | 254 /s | 426 /s | broker call + SQLite write |
| mixed traffic (`max` demo, place + cancel) | – | **≈ 700 /s** | shares the writer with everything else |

So: **one bot can place about 150 orders per second** with an instant broker, but only about **10 per second with a real network**
(the lock is held during the broker call, on purpose: that is what makes the capital check safe). **Many bots together reach about
1,400–1,600 orders per second**, about 6–13 × more than before the optimizations.

One more effect: **a bot gets slower the more orders it has**. The capital check replays all of the bot's fills on every order.
From 1,000 to 6,000 orders, the time per order went from 8.6 ms to 32 ms (before). After the change it is cheaper, but it still
grows in a straight line with the number of fills. The real fix would be to save a running position per bot.

## 4. What broke first, and what I changed

The first run was surprising: with only **50 bots** some requests took more than a second, while the server CPU was at **5 %**. The
per-endpoint breakdown showed that only requests that **write to the database** were slow. The full story is in
[04-specific-interesting-and-hard-things.md](04-specific-interesting-and-hard-things.md). The changes, in the order I found them:

| # | Problem | Change | File |
|---|---|---|---|
| 1 | SQLite has one writer; Microsoft.Data.Sqlite answers a collision with `Thread.Sleep(150)` (blocking a thread) | **WAL mode** + `synchronous=NORMAL`, and an **async write queue** (`SemaphoreSlim`) around `SaveChanges`, so writers never collide | `Api/Data.cs`, `Api/Program.cs` |
| 2 | No index on `Orders.BotId` / `Orders.BrokerId`: every bot request read the whole table | Two indexes (also added to existing databases at startup) | `Api/Data.cs`, `Api/Program.cs` |
| 3 | The API key check did 3 queries per request | One query with joins | `Api/PublicApi.cs` |
| 4 | The capital check loaded every order of the bot as a full tracked entity | Load only the 9 columns the ledger needs | `Api/Services.cs` |
| 5 | The dashboard fetched each account's history **one after another** (400 accounts × 30 ms = 12 s) | Fetch them all in parallel | `Api/Endpoints.cs` |
| 6 | Every tab recomputed the same dashboard / bots page (all users see all accounts) | Share the computed page for 2 s; any change made in the UI clears it at once | `Api/Services.cs`, `Api/Endpoints.cs` |
| 7 | Each key wrote its own "last used" row every 10 s from inside a request (200 writes/s with 2,000 bots) | Count in memory and write all keys in **one** transaction every 10 s | `Api/Services.cs` (`KeyUsage`) |

### Before and after, same test

The `fleet` scenario (real rate limits, 5 bots per user, 30 s per step) on the original code and after changes 1–4:

| Users × 5 bots | Before: req/s (wanted) | Before: p95 | After: req/s (wanted) | After: p95 |
|---|---:|---:|---:|---:|
| 10 (50 bots, the most the default 50-key cap allows) | 76 (88) | 501 ms | 76 (88) | 69 ms |
| 50 (250 bots) | 368 (441) | 1,425 ms | 380 (441) | 65 ms |
| 100 (500 bots) | **283 (883)** | **5,048 ms** (p99 14 s) | **757 (883)** | **72 ms** |
| 200 (1,000 bots) | – (stopped) | – | 1,464 (1,766) | 1,361 ms |

(The "wanted" rate is not reached exactly even when everything is fine, because the browser tabs start at random moments in the step.)
Changes 5–7 came after this run, and together they moved the `max` demo from "1,000 bots" to "1,500 bots" sustained.

**In short: before, the app got slow at about 250 bots and fell over at 500. Now it handles about 1,500 bots with 10 bots per user, and it
does not fall over at 2,000; it only gets slow.**

### What would be next

What limits it now is still **one SQLite writer**: about 700 order operations per second while 1,500 bots are also reading. The next
steps would be grouping several writes into one transaction, or moving to PostgreSQL (many writers). But for an app that is meant for
one person or one team and their bots, I think SQLite is now more than enough: the default configuration allows 50 bots, and the server
handles 30 times that.

## Things to keep in mind about these numbers

- The load generator and the server ran on the **same laptop** and compete for CPU (the report has a "Client CPU" column).
- The simulated broker always answers in exactly 30 ms and never fails. Real Alpaca varies, has its own rate limit (about 200 requests per minute per
  account) and sometimes errors. **In real use, Alpaca's limit would be hit long before OpenTrade's.**
- Every step is short (10 s in the demo, 30 s in `fleet`). A long test (hours) could show problems these do not, like the database
  file growing, or WAL checkpoints.
- The raw reports are in [results/](results/): `before-optimization.md` (the full run before the changes) and
  `max-demo-after-optimization.md` (the demo above, with the per-endpoint breakdown).
