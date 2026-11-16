import { FormEvent, useState } from "react";
import { Link } from "react-router-dom";
import { api, useApi } from "../api";
import { agoMs, num } from "../format";
import type { BotRow, BrokerRow } from "../types";
import { Badge, Confirm, Empty, Field, Head, Metric, Modal, Panel, Table } from "../ui";

interface Key { id: string; name: string; brokerId: string; broker: string; paper: boolean; botId: string | null; bot: string | null; perms: string[]; hint: string; lastUsed: number | null; expires: number; requests: number; expired: boolean }
interface Keys { keys: Key[]; requests: number; active: number; lastUsed: number | null; rateLimit: number }
const LABEL: Record<string, string> = { read: "Read account & orders", positions: "Read positions", trade: "Place & cancel orders" };
const day = (ms: number) => new Date(ms).toLocaleDateString("en-GB", { day: "2-digit", month: "short", year: "numeric" });

export function ApiKeys() {
  const [d, reload] = useApi<Keys>("/keys", 10000);
  const [brokers] = useApi<BrokerRow[]>("/brokers");
  const [bots] = useApi<{ bots: BotRow[] }>("/bots");
  const [form, setForm] = useState(false), [secret, setSecret] = useState(""), [err, setErr] = useState(""), [brokerId, setBrokerId] = useState("");
  if (!d || !brokers || !bots) return null;
  const reveal = (r: { secret: string }) => { setSecret(r.secret); reload(); };
  const bid = brokerId || brokers[0]?.id || "";
  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    try {
      reveal(await api("/keys", "POST", { name: f.get("name"), brokerId: bid, botId: f.get("botId") || null, perms: f.getAll("perm"), expiresInDays: Number(f.get("days")) }));
      setForm(false); setErr("");
    } catch (x) { setErr((x as Error).message); }
  }
  return (
    <>
      <Head title="API Management" sub="Let your own bots trade through OpenTrade. Every key has its own permissions.">
        <Link className="btn" to="/api-docs">REST API docs</Link>
        <button className="btn pri" disabled={!brokers.length} title={brokers.length ? "" : "Connect a broker first"} onClick={() => setForm(true)}>+ Create API key</button>
      </Head>
      <div className="grid4">
        <Metric label="Active API keys" value={d.active} sub={`${d.keys.length - d.active} expired`} />
        <Metric label="Bots with a key" value={new Set(d.keys.map(k => k.botId).filter(Boolean)).size} sub={`${bots.bots.length} bots in total`} />
        <Metric label="Requests served" value={num(d.requests)} sub={d.lastUsed ? `Last used ${agoMs(d.lastUsed)}` : "No requests yet"} />
        <Metric label="Rate limit" value={`${d.rateLimit}/min`} sub="Per API key" />
      </div>
      <Panel title="API keys" note="Secrets are shown only once at creation. Only a hash is stored." action={<Badge tone="blue">API v1</Badge>}>
        {d.keys.length === 0 ? <Empty>No API keys yet.</Empty> : (
          <Table cols="1.4fr .9fr 1.2fr 1.3fr 1.6fr 1fr 1.1fr 1.2fr" head={["Key name", "Account", "API key", "Bot", "Permissions", "Last used", "Expires", "Actions"]}>
            {d.keys.map(k => (
              <div className="tr tall" key={k.id}>
                <span className="name">{k.name}</span>
                <span><Badge tone={k.paper ? "blue" : "green"}>{k.paper ? "PAPER" : "LIVE"}</Badge><small>{k.broker}</small></span>
                <span className="mono muted">{k.paper ? "ot_test_" : "ot_live_"}{k.hint}</span>
                <span>{k.bot ?? <span className="muted">Whole account</span>}</span>
                <span>{k.perms.map(p => LABEL[p] ?? p).join(" · ")}</span>
                <span className="muted">{k.lastUsed ? agoMs(k.lastUsed) : "Never"}<small>{num(k.requests)} requests</small></span>
                <span className={k.expired ? "neg" : ""}>{k.expired ? "Expired" : day(k.expires)}</span>
                <span className="actions" style={{ justifyContent: "flex-start", gap: 18 }}>
                  <Confirm onYes={() => api<{ secret: string }>(`/keys/${k.id}/rotate`, "POST").then(reveal)}>Rotate</Confirm>
                  <Confirm onYes={async () => { if (window.confirm(`Revoke "${k.name}"? Clients using it will stop working.`)) { await api(`/keys/${k.id}`, "DELETE"); reload(); } }}><span className="neg">Revoke</span></Confirm>
                </span>
              </div>
            ))}
          </Table>
        )}
      </Panel>
      <Panel title="Quick start" note="Send the key in the Authorization header.">
        <div className="code">{`curl ${location.origin}/v1/account \\\n  -H "Authorization: Bearer ot_test_YOUR_KEY"\n\ncurl -X POST ${location.origin}/v1/orders \\\n  -H "Authorization: Bearer ot_test_YOUR_KEY" -H "Content-Type: application/json" \\\n  -d '{"symbol":"AAPL","side":"buy","type":"market","qty":1}'`}</div>
      </Panel>
      {form && (
        <Modal title="Create API key" onClose={() => setForm(false)}>
          <form onSubmit={create} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
            <Field label="Key name"><input className="inp" name="name" placeholder="e.g. Momentum bot" required maxLength={60} autoFocus /></Field>
            <div className="row2">
              <Field label="Broker account"><select className="inp" value={bid} onChange={e => setBrokerId(e.target.value)}>{brokers.map(b => <option key={b.id} value={b.id}>{b.name}{b.paper ? " (paper)" : " (live)"}</option>)}</select></Field>
              <Field label="Valid for"><select className="inp" name="days" defaultValue="90"><option value="30">30 days</option><option value="90">90 days</option><option value="180">180 days</option><option value="365">1 year</option></select></Field>
            </div>
            <Field label="Bind to bot (optional)" hint="A bound key only sees this bot's orders and positions, and its orders are attributed to the bot.">
              <select className="inp" name="botId"><option value="">Whole account</option>{bots.bots.filter(b => b.brokerId === bid).map(b => <option key={b.id} value={b.id}>{b.name}</option>)}</select>
            </Field>
            <div className="chk">{Object.entries(LABEL).map(([k, l]) => <label key={k}><input type="checkbox" name="perm" value={k} defaultChecked={k !== "trade"} />{l}</label>)}</div>
            {err && <div className="err" role="alert">{err}</div>}
            <div className="actions"><button type="button" className="btn" onClick={() => setForm(false)}>Cancel</button><button className="btn pri">Create</button></div>
          </form>
        </Modal>
      )}
      {secret && (
        <Modal title="Copy your secret now" onClose={() => setSecret("")}>
          <p className="muted sm">It will not be shown again. Only a hash is stored on the server.</p>
          <div className="secret">{secret}</div>
          <div className="actions"><button className="btn" onClick={() => navigator.clipboard.writeText(secret)}>Copy</button><button className="btn pri" onClick={() => setSecret("")}>Done</button></div>
        </Modal>
      )}
    </>
  );
}
