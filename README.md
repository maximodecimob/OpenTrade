# OpenTrade Manager

A web application for managing algorithmic trading.
It connects to a broker (Alpaca, paper trading), stores its own data in a database, and offers a REST API that external trading bots use to place orders, with permissions per bot, capital allocation and a pause switch.

University project — C# / ASP.NET Core.

| | |
|---|---|
| **Author** | Jacobo Bascón Montalvo |

## Quick start

You only need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone <repository-url>
cd OpenTrade
dotnet run
```

Open http://localhost:5080 and click **Create account**.
The first account you create becomes the administrator.
To see real data, go to **Brokers → Connect broker** and paste paper trading keys from [alpaca.markets](https://alpaca.markets).

Run the tests with:

```bash
dotnet test OpenTrade.slnx
```

## Documentation

Everything else is in the `docs/` folder:

| File | About |
|---|---|
| [01-overview.md](docs/01-overview.md) | What the app is and what you can do with it. |
| [02-architecture.md](docs/02-architecture.md) | How the code is organised, what happens during a request, the data model and the security. |
| [03-load-testing.md](docs/03-load-testing.md) | How much traffic the app can take, and how much CPU and RAM it uses. |
| [04-specific-interesting-and-hard-things.md](docs/04-specific-interesting-and-hard-things.md) | The specific, interesting and hard things in the app. |
| [05-setup-and-usage.md](docs/05-setup-and-usage.md) | Installation, accounts, broker, example bots, tests, configuration, troubleshooting and limitations. |
| [06-bot-api.md](docs/06-bot-api.md) | The public API for bots: endpoints, orders, capital rules and errors. |
