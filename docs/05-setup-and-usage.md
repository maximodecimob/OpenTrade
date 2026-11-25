# 5. Setup and usage

This page explains how to install the app, create the first account, connect a broker, run the example bots and change the web interface.

## Requirements

You only need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
The web interface is already built and included in `wwwroot/`, so Node.js is only needed if you want to change it.

```bash
git clone <repository-url>
cd OpenTrade
dotnet run
```

Then open http://localhost:5080.

## Creating an account

There are no default credentials.
On a fresh install the page shows **Create account**, and the first account you create becomes the administrator.
Every later account is a read-only `viewer`, and an admin can change the role in Settings → Users.

You can use the **Create account** button on the sign-in page, or create the account from a terminal while the app is running.
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

The answer is the new user (`201`) with its role.
Then you can sign in on the web page with that email and password.

A self-registered account is a viewer: it can see everything but it cannot trade or change settings, because it shares the broker data of the administrator.
To give it more rights, sign in as admin and go to Settings → Users.
To turn self-registration off, start the app with `Registration__Enabled=false`.
To choose the role of new accounts (`viewer` or `trader`), use `Registration__Role=trader`.

### Setting the administrator from the command line

This is useful on a server, or to reset a fresh database.
Stop the app, delete the `data/` folder and start it with the admin in the environment.
These variables are only used when no user exists yet, because after that the admin is a normal account like the other users:

```powershell
$env:Admin__Email = "me@example.com"; $env:Admin__Password = "a-long-password-1"; dotnet run
```

To start again from zero, stop the app and delete the `data/` folder.

## Connecting a broker

Without a broker the app starts empty.
To trade with fake money:

1. Create a free account at [alpaca.markets](https://alpaca.markets) and open Paper Trading → API Keys → Generate.
2. In OpenTrade go to Brokers → Connect broker and paste the Key ID and the Secret.

The app checks the keys with Alpaca and then stores them encrypted.
After that, the dashboard, portfolios, positions, orders and assets show your paper account.

## Features

- **Dashboard and portfolios**: equity, daily P&L, equity curve, cash, and how the money is split per asset class and per bot.
- **Orders**: place market and limit orders. The latest price and the estimated cost are shown before you confirm. You can also cancel one order or all open orders, and export them to CSV. Orders are synced from the broker every 15 s, so orders made outside the app also appear.
- **Positions and assets**: open positions with unrealized P&L and a button to close them, and a list of the instruments you can trade, with search.
- **Bots**: register a bot and give it a percentage of the broker account with a slider. The percentages of one account can never add up to more than 100 %, and a bot can never spend more than its share. You can follow its positions, P&L and heartbeat, and pause or resume it.
- **API keys**: each key has permissions (`read`, `positions`, `trade`) and can be bound to one bot. Keys expire and can be rotated or revoked.
- **Users and roles**: `admin` can do everything, `trader` can trade and pause bots, and `viewer` can only read. If a user is disabled, they are signed out at once.

## Example bots

The `Bots/` folder has three strategies.
They connect to OpenTrade only through the public API, like any external bot would:

| Bot | Strategy |
|---|---|
| SMA Crossover | Holds AAPL, MSFT and NVDA while the 10-day moving average is above the 30-day one, otherwise it stays in cash. |
| RSI Mean Reversion | Buys SPY, QQQ and AAPL when the 14-day RSI drops below 30, and sells when it goes above 55. |
| DCA Index Buyer | Buys 50 USD of SPY and QQQ every 24 hours until its allocation is used. |

With OpenTrade running and a broker connected, run these from the repository root:

```bash
dotnet run --project Bots -- setup                      # creates the 3 bots (10% of the account each) and their API keys; asks for the admin login
dotnet run --project Bots -- run all --dry-run --once   # shows what they would trade, places nothing
dotnet run --project Bots -- run all                    # runs them (Ctrl+C to stop)
```

To run only one bot, use `run sma`, `run rsi` or `run dca`.
`dotnet run --project Bots -- help` shows all the options.

## Tests

```bash
dotnet test OpenTrade.slnx
```

There are more than 90 tests:

- strategy maths, the P&L ledger, order validation, password hashing and Alpaca JSON parsing (with recorded responses);
- every API endpoint, roles, API-key scoping, account creation, capital limits and idempotent orders, with a fake broker so no Alpaca account is needed;
- the three bots running end-to-end against the real API.

The load tests are explained in [03-load-testing.md](03-load-testing.md).

## Configuration

The rate limits and the maximum number of API keys are all together in `limits.json`, in the root folder.
The other settings go in `appsettings.json`.
An environment variable always wins over both files.

| Setting | Default | Meaning |
|---|---|---|
| `RateLimit:V1PerMinute` | 120 | Requests per minute per API key on `/v1`. |
| `RateLimit:GlobalPerMinute` | 600 | Requests per minute per IP, for the web interface (not for the bot API). |
| `RateLimit:BadKeyPerMinute` | 30 | Requests with a missing, wrong or expired API key, per minute per IP. |
| `RateLimit:LoginPerMinute` | 5 | Login and register attempts per minute per IP. |
| `Keys:MaxCount` | 50 | Maximum number of API keys in the whole app. |
| `Registration:Enabled` | true | Allows self-registration. |
| `Registration:Role` | viewer | Role of self-registered accounts (`viewer` or `trader`). |
| `Brokers:AllowLive` | false | Allows live (real money) brokers. |

In environment variables, `:` is written as `__` (for example `Registration__Enabled=false`).

## Changing the web interface

You need Node.js 18 or newer.

```bash
cd web
npm install
npm run dev       # http://localhost:5173, forwards /api to the backend on :5080
npm run build     # writes to ../wwwroot
```

Vite fails if the folder path contains `#` (for example `C:\...\C#\OpenTrade`), so use a path without `#` to build the frontend.

## Troubleshooting

| Problem | Solution |
|---|---|
| "Invalid email or password" | The account does not exist or the password is wrong. Create a new one with Create account, or stop the app, delete `data/` and start again to make a new first (admin) account. |
| Port 5080 already in use | Another copy is still running. Close it, or run `dotnet run -- --urls http://localhost:5090`. |
| Build error "file is being used by another process" | Stop the running app (Ctrl+C) before building or testing. |
| Broker shows an error | The Alpaca keys were regenerated, or paper and live keys are mixed up. Use Brokers → Update keys. |

## Limitations

- The database schema is created at startup with `EnsureCreated`, without migrations. A small upgrade step at startup moves older databases to the percentage-based allocation (the old fixed USD amounts start again at 0 %, so you have to set the sliders again).
- The P&L of a bot is calculated from its own fills with the average-cost method. Bots cannot sell short, because the API refuses a sell larger than what the bot holds.
- The app uses plain HTTP on localhost. To make it public, it should be behind an HTTPS reverse proxy.
- Market data comes from the free IEX feed of Alpaca and can be a bit delayed.
