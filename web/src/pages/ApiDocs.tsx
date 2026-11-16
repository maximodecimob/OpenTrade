import { Link } from "react-router-dom";
import { Badge, Head, Panel, Table } from "../ui";

const ROWS: [string, string, string, string][] = [
  ["GET", "/v1/account", "read", "Equity, cash and buying power of the key's broker account (plus the bot, if the key is bound to one)."],
  ["GET", "/v1/positions", "positions", "Open positions. A bot-bound key sees only what that bot holds."],
  ["GET", "/v1/orders?status=open&limit=50", "read", "List orders (status: open, filled, cancelled, rejected, pending, partial)."],
  ["GET", "/v1/orders/{id}", "read", "One order."],
  ["POST", "/v1/orders", "trade", "Place an order. Returns 201 with the order."],
  ["DELETE", "/v1/orders/{id}", "trade", "Cancel an open order."],
  ["GET", "/v1/bot", "read", "State of the bot this key is bound to (running, allocated)."],
  ["POST", "/v1/heartbeat", "read", "Tell OpenTrade the bot is alive. Any authenticated call also counts as a heartbeat."],
];

export function ApiDocs() {
  const o = location.origin;
  return (
    <>
      <Head title="API documentation" sub="Connect your own bots to OpenTrade over REST."><Link className="btn pri" to="/api-keys">Manage API keys</Link></Head>
      <Panel title="Authentication" badge={<Badge tone="blue">v1</Badge>}>
        <div className="doc">
          <p>Create a key in <Link className="link" to="/api-keys">API Management</Link> and send it with every request:</p>
          <div className="code" style={{ margin: "10px 0" }}>Authorization: Bearer ot_test_xxxxxxxxxxxxxxxx</div>
          <p>A key belongs to one broker account and can optionally be bound to one bot. Bound keys only see and cancel that bot's orders, and their orders are attributed to the bot. If the bot is paused in OpenTrade, <code>POST /v1/orders</code> answers <code>423</code>. Keys are rate limited to 120 requests per minute (<code>429</code>). Errors look like <code>{`{ "error": "message" }`}</code>.</p>
        </div>
      </Panel>
      <Panel title="Endpoints">
        <Table cols=".7fr 2.2fr .9fr 4fr" head={["Method", "Path", "Permission", "Description"]}>
          {ROWS.map(([m, p, perm, d]) => <div className="tr tall" key={m + p}><b className={m === "GET" ? "acc" : m === "POST" ? "pos" : "neg"}>{m}</b><span className="mono">{p}</span><span><Badge tone="grey">{perm}</Badge></span><span className="muted">{d}</span></div>)}
        </Table>
      </Panel>
      <Panel title="Place an order">
        <div className="doc">
          <div className="code">{`curl -X POST ${o}/v1/orders \\\n  -H "Authorization: Bearer ot_test_xxxxxxxx" \\\n  -H "Content-Type: application/json" \\\n  -d '{\n    "symbol": "AAPL",\n    "side": "buy",            // buy | sell\n    "type": "limit",          // market | limit\n    "qty": 10,\n    "limitPrice": 225.50,     // required for limit orders\n    "timeInForce": "day"      // day | gtc\n  }'`}</div>
          <h3>Response</h3>
          <div className="code">{`{ "id": "OT-3F9A1C0B7D", "symbol": "AAPL", "side": "buy", "type": "limit",\n  "qty": 10, "filled": 0, "limitPrice": 225.5, "avgFillPrice": null,\n  "status": "pending", "timeInForce": "day", "createdAt": "2026-11-12T14:30:00.000Z" }`}</div>
          <h3>Python example</h3>
          <div className="code">{`import requests\nH = {"Authorization": "Bearer ot_test_xxxxxxxx"}\nprint(requests.get("${o}/v1/account", headers=H).json())\nr = requests.post("${o}/v1/orders", headers=H, json={"symbol": "AAPL", "side": "buy", "type": "market", "qty": 1})\nprint(r.status_code, r.json())`}</div>
        </div>
      </Panel>
    </>
  );
}
