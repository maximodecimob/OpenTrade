# OpenTrade load tests

A console program that measures how much traffic OpenTrade can take and what it costs in CPU and memory.
The results and what they mean are written up in [docs/03-load-testing.md](../docs/03-load-testing.md).

## Quick demo: the maximum traffic (about 70 seconds)

```powershell
dotnet build LoadTests -c Release
dotnet LoadTests/bin/Release/net10.0/OpenTrade.LoadTests.dll max
```

It adds users (10, 50, 100, 150, 200, 300), each with 10 bots and a browser tab, in 10-second steps, stops at the first step where the
server cannot keep up, then does one burst with no pauses. At the end it prints the **maximum sustained** traffic (users × bots,
requests/s, order operations/s, CPU, RAM) and the **maximum burst**. Options:

```powershell
dotnet LoadTests/bin/Release/net10.0/OpenTrade.LoadTests.dll max --users 100 --bots 10              # only "100 users x 10 bots"
dotnet LoadTests/bin/Release/net10.0/OpenTrade.LoadTests.dll max --users 20,100,500 --seconds 15    # your own steps
```

## The detailed scenarios (about 15 minutes)

```powershell
dotnet LoadTests/bin/Release/net10.0/OpenTrade.LoadTests.dll all            # every scenario
dotnet LoadTests/bin/Release/net10.0/OpenTrade.LoadTests.dll fleet --quick  # one scenario with short steps
```

Scenarios: `limits`, `fleet`, `ceiling`, `orders` (or `all`). Options: `--quick` (short steps), `--max-users N` (fleet ramp, default 800),
`--bots N` (bots per user, default 5). Use Release: a Debug build is a lot slower and gives wrong numbers.

The report is printed and also saved as `LoadTests/results/latest.md` (plus a copy with the date). The folder is git-ignored.

## How it works

- **The real app.** The program starts OpenTrade (the same `Program.cs`, middleware, SQLite database and rate limiter) on a real
  Kestrel socket, using `WebApplicationFactory.UseKestrel()`. It runs in a **child process** (`... server --port ...`), so the CPU and
  memory we measure are the server's only, not the load generator's. Every 250 ms the server prints a `STATS` line (CPU, working set,
  GC heap, threads, database size) and the parent reads these lines.
- **A simulated broker.** Alpaca is replaced by `SimBroker` (thread-safe, in memory, with a configurable delay that imitates the network).
  Alpaca's own limit is about 200 requests per minute, so testing against it would only measure Alpaca. Nothing is sent anywhere.
- **Simulated users with their own IPs.** OpenTrade trusts `X-Forwarded-For` from the local machine (it expects a reverse proxy),
  so each simulated user sends a different IP (`10.x.y.z`). That way the per-IP limits (web interface, wrong keys) apply per user, as in production.
- **Seeding through the API.** Users, broker accounts, bots and API keys are created with the same admin endpoints the web UI uses.
- Only for testing, the server can raise the limits (`RateLimit:V1PerMinute`, `RateLimit:GlobalPerMinute`, `RateLimit:LoginPerMinute`,
  `Keys:MaxCount`). The defaults are the real ones (120, 600, 5, 50). The wrong-key limit (`RateLimit:BadKeyPerMinute`, 30) is not raised.

| Scenario | Question it answers |
|---|---|
| `max` | The short demo: up to how many users × bots does the server keep up, and what does it cost? (limits raised) |
| `limits` | With the default configuration, do the limits work, and exactly how many requests get through? |
| `fleet` | Many users, each with a browser tab and 5 bots at 100 req/min: how many users before it gets slow? |
| `ceiling` | With the limits off, how many requests per second can one endpoint serve? |
| `orders` | How many order operations per second, and what limits them (per-bot lock, SQLite writes, broker latency, order history)? |
