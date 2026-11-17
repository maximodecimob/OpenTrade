import { ReactNode } from "react";
import { Link } from "react-router-dom";
import { Badge, Head, Panel, Table } from "../ui";

type Row = [method: string, path: string, perm: string, description: string];

const ROWS: Row[] = [
  ["GET", "/v1/account", "read", "Equity, cash and buying power of the key's broker account. With a bot-bound key it also returns the bot, its allocation in % and in USD."],
  ["GET", "/v1/bot", "read", "The bot this key is bound to: running, allocated (USD at the current equity) and allocationPct. 404 for a key that is not bound to a bot."],
  ["POST", "/v1/heartbeat", "read", "Tell OpenTrade the bot is alive. Any authenticated call already counts as a heartbeat; this one is just the cheapest."],
  ["GET", "/v1/positions", "positions", "Open positions. A bot-bound key only sees what that bot holds (rebuilt from its own fills)."],
  ["GET", "/v1/bars/{symbol}", "read", "Price history, oldest first. Query: timeframe = 1Min | 5Min | 15Min | 1Hour | 1Day, limit = 1-1000 (default 100). Crypto symbols contain a slash, e.g. BTC/USD."],
  ["GET", "/v1/orders", "read", "List orders, newest first. Query: status = open | pending | partial | filled | cancelled | rejected, limit = 1-100 (default 50)."],
  ["GET", "/v1/orders/{id}", "read", "One order, by the id OpenTrade returned (or your clientOrderId)."],
  ["POST", "/v1/orders", "trade", "Place an order. 201 with the order, or 200 with the original order when the clientOrderId was already used."],
  ["DELETE", "/v1/orders/{id}", "trade", "Cancel an open order. 204 on success."],
];

const ERRORS: [string, string, string][] = [
  ["400", "Bad request", "A field is missing or invalid (symbol, side, type, qty, limitPrice, timeInForce, clientOrderId, timeframe). The message says which."],
  ["401", "Unauthorized", "No key, unknown key or expired key."],
  ["403", "Forbidden", "The key does not have the permission this endpoint needs (read, positions, trade)."],
  ["404", "Not found", "The order does not exist, or belongs to another bot."],
  ["409", "Conflict", "The order was refused by a rule: it exceeds the bot's capital allocation, sells more than the bot holds, the broker rejected it, the order is no longer open, or the clientOrderId belongs to another order."],
  ["423", "Locked", "The bot is paused in OpenTrade. Orders are refused until someone resumes it."],
  ["429", "Too many requests", "More than 120 requests in a minute with this key. Wait for the next minute."],
  ["502", "Bad gateway", "The broker (Alpaca) could not be reached or rejected OpenTrade's credentials."],
];

const Code = ({ children }: { children: string }) => <div className="code" style={{ margin: "8px 0 4px" }}>{children}</div>;
const H = ({ id, children }: { id: string; children: ReactNode }) => <h3 id={id}>{children}</h3>;

export function ApiDocs() {
  const o = location.origin;
  const K = "ot_test_xxxxxxxxxxxxxxxx";
  return (
    <>
      <Head title="API documentation" sub="Connect your own trading bots to OpenTrade over REST."><Link className="btn pri" to="/api-keys">Manage API keys</Link></Head>

      <Panel title="Quick start" badge={<Badge tone="blue">v1</Badge>} note="From zero to a first order in four steps.">
        <div className="doc">
          <ol style={{ margin: "0 0 0 18px", padding: 0, lineHeight: 1.9, fontSize: 13 }} className="muted">
            <li>Sign in as administrator and connect a broker in <Link className="link" to="/brokers">Brokers</Link>.</li>
            <li>Create a bot in <Link className="link" to="/bots">Strategies &amp; Bots</Link> and give it a share of the account with the slider (for example 10%).</li>
            <li>In <Link className="link" to="/api-keys">API Management</Link>, create a key bound to that bot with the permissions <code>read</code>, <code>positions</code> and <code>trade</code>. The secret is shown once.</li>
            <li>Call the API:</li>
          </ol>
          <Code>{`curl ${o}/v1/account -H "Authorization: Bearer ${K}"`}</Code>
          <p>Base URL: <code>{o}</code>. Everything is JSON over HTTP. Times are ISO 8601 in UTC. Numbers are plain JSON numbers (prices in USD).</p>
        </div>
      </Panel>

      <Panel title="Authentication" note="One header on every request.">
        <div className="doc">
          <Code>{`Authorization: Bearer ${K}`}</Code>
          <p>An <code>X-API-Key: &lt;key&gt;</code> header works too. Keys starting with <code>ot_test_</code> belong to paper accounts, <code>ot_live_</code> to live accounts.</p>
          <H id="perms">Permissions</H>
          <p><code>read</code>: account, bot, bars, orders and heartbeat. <code>positions</code>: positions. <code>trade</code>: place and cancel orders. A key has one or more of them and an expiry date (1-365 days).</p>
          <H id="scope">Bot-bound keys</H>
          <p>A key belongs to one broker account and can be bound to one bot. A bound key only sees and cancels <em>its own</em> orders, <code>/v1/positions</code> shows only what that bot holds, and every order it places is attributed to the bot. An unbound key sees the whole broker account.</p>
          <p>Keep keys secret: they are stored only as a hash, so they cannot be recovered. If one leaks, rotate or delete it in API Management.</p>
        </div>
      </Panel>

      <Panel title="Endpoints">
        <Table cols=".7fr 2fr .9fr 4.4fr" head={["Method", "Path", "Permission", "Description"]}>
          {ROWS.map(([m, p, perm, d]) => <div className="tr tall" key={m + p}><b className={m === "GET" ? "acc" : m === "POST" ? "pos" : "neg"}>{m}</b><span className="mono">{p}</span><span><Badge tone="grey">{perm}</Badge></span><span className="muted">{d}</span></div>)}
        </Table>
      </Panel>

      <Panel title="Place an order" badge={<Badge tone="grey">POST /v1/orders</Badge>}>
        <div className="doc">
          <p>Body (JSON):</p>
          <Table cols="1.2fr .9fr .9fr 4fr" head={["Field", "Type", "Required", "Meaning"]}>
            {([
              ["symbol", "string", "yes", "Ticker, e.g. AAPL, or a crypto pair such as BTC/USD. Up to 15 characters: A-Z, 0-9, . / -"],
              ["side", "string", "yes", "buy or sell"],
              ["type", "string", "no", "market (default) or limit"],
              ["qty", "number", "yes", "Quantity, greater than 0. Fractions are allowed for assets that support them."],
              ["limitPrice", "number", "limit only", "Maximum price for a buy, minimum for a sell"],
              ["timeInForce", "string", "no", "day (default) or gtc (good till cancelled)"],
              ["clientOrderId", "string", "no", "Your own unique id (8-48 characters: letters, digits, - or _). Makes retries safe, see below."],
            ] as const).map(([f, t, r, d]) => <div className="tr tall" key={f}><span className="mono">{f}</span><span className="muted">{t}</span><span className="muted">{r}</span><span className="muted">{d}</span></div>)}
          </Table>
          <Code>{`curl -X POST ${o}/v1/orders \\\n  -H "Authorization: Bearer ${K}" \\\n  -H "Content-Type: application/json" \\\n  -d '{\n    "symbol": "AAPL",\n    "side": "buy",\n    "type": "limit",\n    "qty": 10,\n    "limitPrice": 225.50,\n    "timeInForce": "day",\n    "clientOrderId": "sma-AAPL-2026-11-17"\n  }'`}</Code>
          <H id="order">The order object</H>
          <Code>{`{\n  "id": "sma-AAPL-2026-11-17",\n  "symbol": "AAPL",\n  "side": "buy",                 // buy | sell\n  "type": "limit",               // market | limit\n  "qty": 10,\n  "filled": 0,                   // quantity filled so far\n  "limitPrice": 225.5,           // null for market orders\n  "avgFillPrice": null,          // null until something is filled\n  "status": "pending",           // pending | partial | filled | cancelled | rejected\n  "timeInForce": "day",\n  "createdAt": "2026-11-17T14:30:00.000Z"\n}`}</Code>
          <p>The order is a request to the broker: <code>status</code> changes when it is filled or cancelled. OpenTrade syncs with the broker every 15 seconds, so poll <code>GET /v1/orders/{"{id}"}</code> to follow it.</p>
          <H id="capital">Capital rules (bot-bound keys)</H>
          <p>Every bot has an allocation: a percentage of its broker account (the percentages of one account never add up to more than 100%). OpenTrade checks each order before it reaches the broker and answers <code>409</code> with an explanation when:</p>
          <ul className="muted" style={{ margin: "6px 0 0 18px", fontSize: 13, lineHeight: 1.8 }}>
            <li>a <strong>buy</strong> would make the bot's positions plus its open buy orders worth more than its allocation (market orders are priced at the latest quote);</li>
            <li>the bot has <strong>no capital</strong> allocated (0%), so it cannot buy at all;</li>
            <li>a <strong>sell</strong> is larger than what the bot holds and is not already selling. Bots cannot go short.</li>
          </ul>
          <H id="idempotency">Retrying safely: clientOrderId</H>
          <p>If a request times out you cannot tell whether the order was placed. Send the same <code>clientOrderId</code> again: if the order already exists you get <code>200</code> and the original order back and nothing new reaches the broker. Without a <code>clientOrderId</code> OpenTrade generates an id and a retry would create a second order. Use something deterministic, such as the strategy, the symbol and the day.</p>
        </div>
      </Panel>

      <Panel title="Other examples">
        <div className="doc">
          <H id="bars">Price history</H>
          <Code>{`curl "${o}/v1/bars/AAPL?timeframe=1Day&limit=30" -H "Authorization: Bearer ${K}"\n\n[ { "t": "2026-11-14T05:00:00.000Z", "open": 225.1, "high": 227.4, "low": 224.0, "close": 226.8, "volume": 51234000 }, ... ]`}</Code>
          <H id="positions">Positions</H>
          <Code>{`curl ${o}/v1/positions -H "Authorization: Bearer ${K}"\n\n[ { "symbol": "AAPL", "qty": 10, "avgEntryPrice": 225.5, "currentPrice": 226.8, "marketValue": 2268, "unrealizedPl": 13 } ]`}</Code>
          <H id="bot">Bot state and allocation</H>
          <Code>{`curl ${o}/v1/bot -H "Authorization: Bearer ${K}"\n\n{ "id": "a1b2c3d4e5", "name": "SMA Crossover", "running": true,\n  "allocated": 10000.0,        // USD, at the account's current equity\n  "allocationPct": 10.0,       // percent of the account\n  "serverTime": "2026-11-17T14:30:00Z" }`}</Code>
          <H id="cancel">Cancel an order</H>
          <Code>{`curl -X DELETE ${o}/v1/orders/OT-3F9A1C0B7D -H "Authorization: Bearer ${K}"   # 204 No Content`}</Code>
          <H id="python">Python</H>
          <Code>{`import requests\n\nBASE, H = "${o}", {"Authorization": "Bearer ${K}"}\n\nbot = requests.get(f"{BASE}/v1/bot", headers=H).json()\nif not bot["running"]:\n    raise SystemExit("bot is paused")                 # orders would get HTTP 423\n\nr = requests.post(f"{BASE}/v1/orders", headers=H, json={\n    "symbol": "AAPL", "side": "buy", "type": "market", "qty": 1,\n    "clientOrderId": "demo-AAPL-2026-11-17",          # safe to retry\n})\nprint(r.status_code, r.json())                        # 201 the first time, 200 on a retry`}</Code>
          <H id="csharp">C#</H>
          <Code>{`using var http = new HttpClient { BaseAddress = new Uri("${o}") };\nhttp.DefaultRequestHeaders.Authorization = new("Bearer", "${K}");\n\nvar res = await http.PostAsJsonAsync("/v1/orders", new\n{\n    symbol = "AAPL", side = "buy", type = "market", qty = 1,\n    clientOrderId = "demo-AAPL-2026-11-17",\n});\nConsole.WriteLine($"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");`}</Code>
          <p>The complete example bots (SMA crossover, RSI mean reversion, DCA) live in the <code>Bots/</code> folder of the repository and only use this API.</p>
        </div>
      </Panel>

      <Panel title="Errors and limits">
        <div className="doc">
          <p>Errors always have the same shape, so a bot can show the message as it is:</p>
          <Code>{`HTTP/1.1 409 Conflict\n{ "error": "Order exceeds the bot's allocation: it would use 12,400.00 of 10,000.00 USD (10% of the account)" }`}</Code>
        </div>
        <Table cols=".6fr 1.2fr 5fr" head={["Status", "Meaning", "When"]}>
          {ERRORS.map(([c, m, w]) => <div className="tr tall" key={c}><b className={c.startsWith("4") ? "neg" : "acc"}>{c}</b><span>{m}</span><span className="muted">{w}</span></div>)}
        </Table>
        <div className="doc"><p style={{ marginTop: 14 }}><strong>Rate limit:</strong> 120 requests per minute per key (answered with <code>429</code> afterwards). A bot that checks the market every few minutes is far below it. <strong>Paper vs live:</strong> live trading is disabled unless the server is started with <code>Brokers:AllowLive=true</code>.</p></div>
      </Panel>
    </>
  );
}
