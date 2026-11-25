# 6. Bot API

This is a summary of the public API that the bots use.
The complete documentation, always up to date and with examples in curl, Python and C#, is inside the app under **API documentation**.

## Authentication

Every request sends a key created in API Management, as `Authorization: Bearer ot_test_...` (or in the `X-API-Key` header).

## Endpoints

| Method | Path | Permission | Description |
|---|---|---|---|
| GET | `/v1/account` | read | balance of the broker account (and the bot's allocation, for a key bound to a bot) |
| GET | `/v1/bot` · POST `/v1/heartbeat` | read | bot state: running, `allocated` (USD) and `allocationPct` |
| GET | `/v1/bars/{symbol}?timeframe=1Day&limit=100` | read | price history, oldest first |
| GET | `/v1/positions` | positions | positions (only the bot's own if the key is bound to a bot) |
| GET | `/v1/orders?status=open&limit=50`, `/v1/orders/{id}` | read | orders |
| POST | `/v1/orders` | trade | place an order |
| DELETE | `/v1/orders/{id}` | trade | cancel an open order |

## Placing an order

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
| `clientOrderId` | no | your own unique id (8–48 characters: letters, digits, `-`, `_`) |

## Idempotency

If a bot sends the same `clientOrderId` again (for example after a timeout), the answer is `200` with the original order, and nothing new is placed.
A first request returns `201`.

## Capital rules

These rules apply to keys bound to a bot.
A bot can only spend its allocation: a buy is refused with `409` if the bot's positions plus its open buy orders would be worth more than `allocationPct` of the account.
Market orders are priced with the latest quote, and a bot with 0 % cannot buy at all.
A sell larger than what the bot holds is also refused, so there is no short selling.
The check runs before the order goes to the broker, one order at a time per bot.
## Errors

Errors always look like `{ "error": "message" }`:

| Code | Meaning |
|---|---|
| `400` | invalid input |
| `401` | missing, invalid or expired key |
| `403` | missing permission |
| `404` | not found |
| `409` | refused by a rule (capital, holdings, broker rejection, duplicate id) |
| `423` | the bot is paused |
| `429` | more than 120 requests per minute for this key |
| `502` | the broker cannot be reached |
