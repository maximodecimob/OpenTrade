import { FormEvent, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, useApi } from "../api";
import { agoMs, hms, money, money0, pct, signed, spct, tone } from "../format";
import type { BotRow, BrokerRow, Order } from "../types";
import { Badge, Confirm, Empty, Field, Head, Metric, Modal, Panel, Progress, StatusBadge, Table, useMe } from "../ui";

interface Summary { running: number; paused: number; live: number; simulated: number; alloc: number; deployed: number; util: number; pnl: number; pnlPct: number; backed: number }

const Heartbeat = ({ b }: { b: BotRow }) => b.heartbeat == null
  ? <span className="muted">Never</span>
  : <span className="muted"><span className={"dotl" + (b.online ? " on" : "")} />{agoMs(b.heartbeat)}</span>;

function BotForm({ bot, brokers, onClose, onDone }: { bot?: BotRow; brokers: BrokerRow[]; onClose: () => void; onDone: (created?: { id: string; clientId: string }) => void }) {
  const [err, setErr] = useState(""), [busy, setBusy] = useState(false);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const body = { name: f.get("name"), class: f.get("class"), description: f.get("description"), brokerId: f.get("brokerId") ?? bot?.brokerId, allocated: Number(f.get("allocated") || 0) };
    setBusy(true); setErr("");
    try {
      if (bot) { await api(`/bots/${bot.id}`, "PUT", body); onDone(); }
      else onDone(await api<{ id: string; clientId: string }>("/bots", "POST", body));
    } catch (x) { setErr((x as Error).message); setBusy(false); }
  }
  return (
    <Modal title={bot ? "Edit bot" : "Connect bot"} onClose={onClose}>
      <form onSubmit={submit} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
        <Field label="Name"><input className="inp" name="name" defaultValue={bot?.name} required maxLength={60} autoFocus /></Field>
        <div className="row2">
          <Field label="Strategy type"><input className="inp" name="class" defaultValue={bot?.class} placeholder="e.g. US equities" maxLength={40} /></Field>
          <Field label="Allocated capital (USD)"><input className="inp" name="allocated" type="number" min="0" step="any" defaultValue={bot?.allocated ?? 0} /></Field>
        </div>
        <Field label="Description"><input className="inp" name="description" defaultValue={bot?.description} maxLength={200} /></Field>
        {!bot && (
          <Field label="Broker account">
            <select className="inp" name="brokerId" required>{brokers.map(b => <option key={b.id} value={b.id}>{b.name}{b.paper ? " (paper)" : ""}</option>)}</select>
          </Field>
        )}
        {err && <div className="err" role="alert">{err}</div>}
        <div className="actions"><button type="button" className="btn" onClick={onClose}>Cancel</button><button className="btn pri" disabled={busy}>{bot ? "Save" : "Create bot"}</button></div>
      </form>
    </Modal>
  );
}

export function Bots() {
  const { isAdmin, canTrade } = useMe();
  const [d, reload] = useApi<{ bots: BotRow[]; summary: Summary }>("/bots", 5000);
  const [brokers] = useApi<BrokerRow[]>("/brokers");
  const [q, setQ] = useState(""), [st, setSt] = useState(""), [form, setForm] = useState(false), [created, setCreated] = useState<{ id: string; clientId: string } | null>(null);
  if (!d || !brokers) return null;
  const s = d.summary;
  const rows = d.bots.filter(b => b.name.toLowerCase().includes(q.toLowerCase()) && (!st || b.status === st));
  const toggle = (b: BotRow) => api(`/bots/${b.id}/${b.running ? "pause" : "resume"}`, "POST").then(reload);
  return (
    <>
      <Head title="Strategies & Bots" sub="Monitor performance. Control how your capital works.">
        <Link className="btn" to="/api-docs">API documentation</Link>
        {isAdmin && <button className="btn pri" disabled={!brokers.length} title={brokers.length ? "" : "Connect a broker first"} onClick={() => setForm(true)}>+ Connect bot</button>}
      </Head>
      <div className="grid4">
        <Metric label="Bot status" value={`${s.running} running`} sub={`${s.paused} paused`} />
        <Metric label="Capital allocated" value={money(s.alloc)} sub={`${s.backed} capital-backed strateg${s.backed === 1 ? "y" : "ies"}`} />
        <Metric label="Capital utilization" value={pct(s.util)} sub={`${money0(s.deployed)} held in positions`} />
        <Metric label="Bot P&L" value={signed(s.pnl)} sub={`${spct(s.pnlPct)} of allocated capital`} tone={tone(s.pnl)} />
      </div>
      <section className="card panel">
        <div className="tools" style={{ padding: "0 0 14px" }}>
          <input className="inp" style={{ flex: "0 0 180px" }} placeholder="Search bots…" value={q} onChange={e => setQ(e.target.value)} maxLength={40} aria-label="Search bots" />
          <select className="inp" value={st} onChange={e => setSt(e.target.value)}><option value="">All statuses</option><option value="running">Running</option><option value="paused">Paused</option><option value="simulated">Paper</option></select>
          <span className="muted sm" style={{ marginLeft: "auto" }}>{d.bots.length} bots</span>
        </div>
        {d.bots.length === 0 ? <Empty>No bots yet. A bot is an external program that trades through the API with its own key.</Empty> : (
          <Table cols="2fr 1fr 1.3fr 1.5fr 1fr 1.1fr 1.2fr" head={["Strategy / Bot", "Status", "Allocated capital", "Utilization", "Return", "Last heartbeat", "Actions"]}>
            {rows.map(b => (
              <div className="tr tall" key={b.id}>
                <span><span className="name">{b.name}</span><small>{b.class} · {b.broker}</small></span>
                <span><StatusBadge s={b.status} /></span>
                <span className="mono">{money(b.allocated)}</span>
                <span>{(b.utilization * 100).toFixed(1)}%<Progress value={b.utilization} thin color={b.paper ? "var(--pu)" : undefined} /></span>
                <span className={b.returnPct == null ? "muted" : tone(b.pnl)}>{b.returnPct == null ? "—" : spct(b.returnPct)}</span>
                <Heartbeat b={b} />
                <span className="actions" style={{ justifyContent: "flex-start", gap: 14 }}>
                  <Link className="link" to={`/bots/${b.id}`}>Details</Link>
                  {canTrade && <Confirm onYes={() => toggle(b)}>{b.running ? <span className="muted">Pause</span> : <span className="pos">Resume</span>}</Confirm>}
                </span>
              </div>
            ))}
          </Table>
        )}
        <div className="row sm muted" style={{ borderTop: "1px solid var(--bd)", paddingTop: 12 }}><span>Showing {rows.length} of {d.bots.length} bots</span><span>Paused bots are refused by the API (HTTP 423)</span></div>
      </section>
      {form && <BotForm brokers={brokers} onClose={() => setForm(false)} onDone={c => { setForm(false); setCreated(c ?? null); reload(); }} />}
      {created && (
        <Modal title="Bot created" onClose={() => setCreated(null)}>
          <p className="muted sm">Client ID</p><div className="secret">{created.clientId}</div>
          <p className="muted sm">Next step: create an API key bound to this bot in API Management. Orders sent with that key are attributed to the bot.</p>
          <div className="actions"><Link className="btn" to="/api-keys" onClick={() => setCreated(null)}>Create API key</Link><button className="btn pri" onClick={() => setCreated(null)}>Done</button></div>
        </Modal>
      )}
    </>
  );
}

interface Detail {
  bot: BotRow;
  positions: { symbol: string; qty: number; avg: number; last: number; value: number; pnl: number }[];
  orders: Order[];
  keys: { id: string; name: string; hint: string; lastUsed: number | null; perms: string[] }[];
}

export function BotDetail() {
  const { id = "" } = useParams();
  const nav = useNavigate();
  const { isAdmin, canTrade } = useMe();
  const [d, reload] = useApi<Detail>(`/bots/${id}`, 5000);
  const [edit, setEdit] = useState(false);
  const [brokers] = useApi<BrokerRow[]>("/brokers");
  if (!d || !brokers) return null;
  const { bot: b, positions, orders, keys } = d;
  const open = orders.filter(o => o.status === "pending" || o.status === "partial");
  return (
    <>
      <Head title={b.name} sub={`${b.description || b.class} / ${b.broker}`}
        badge={<span className="actions"><StatusBadge s={b.status} /><Badge tone={b.paper ? "blue" : "green"}>{b.paper ? "PAPER" : "LIVE"}</Badge></span>}>
        <div style={{ textAlign: "right" }}>
          <div className="actions">
            {canTrade && <button className="btn" onClick={() => api(`/bots/${b.id}/${b.running ? "pause" : "resume"}`, "POST").then(reload)}>{b.running ? "‖ Pause bot" : "▶ Resume bot"}</button>}
            {isAdmin && <button className="btn" onClick={() => setEdit(true)}>Edit</button>}
            {isAdmin && <button className="btn" onClick={async () => { if (window.confirm(`Delete "${b.name}" and its API keys? Its order history is kept.`)) { await api(`/bots/${b.id}`, "DELETE"); nav("/bots"); } }}><span className="neg">Delete</span></button>}
          </div>
          <div className="muted sm" style={{ marginTop: 8 }}>Bot ID: {b.clientId} · Heartbeat {b.heartbeat == null ? "never" : agoMs(b.heartbeat)}</div>
        </div>
      </Head>
      <div className="grid4">
        <Metric label="Allocated capital" value={money(b.allocated)} sub={`${money0(b.invested)} in positions · ${(b.utilization * 100).toFixed(1)}% utilization`} />
        <Metric label="Total P&L" value={signed(b.pnl)} sub={b.returnPct == null ? "Allocate capital to see a return" : `${spct(b.returnPct)} of allocation`} tone={tone(b.pnl)} />
        <Metric label="Realized P&L" value={signed(b.realized)} sub="From closed trades" tone={tone(b.realized)} />
        <Metric label="Unrealized P&L" value={signed(b.unrealized)} sub="From open positions" tone={tone(b.unrealized)} />
      </div>
      <div className="cols" style={{ gridTemplateColumns: "1fr 320px", alignItems: "start" }}>
        <div className="cols" style={{ gridTemplateColumns: "1fr" }}>
          <Panel title="Open positions" note="Rebuilt from this bot's fills">
            {positions.length === 0 ? <Empty>No open positions.</Empty> : (
              <Table cols="1.3fr 1fr 1fr 1fr 1.2fr 1.2fr" head={["Instrument", "Quantity", "Avg. entry", "Last price", "Market value", "Unrealized P&L"]}>
                {positions.map(p => (
                  <div className="tr" key={p.symbol}><b>{p.symbol}</b><span>{p.qty}</span><span>{money(p.avg)}</span><span>{money(p.last)}</span><span>{money(p.value)}</span><span className={tone(p.pnl)}>{signed(p.pnl)}</span></div>
                ))}
              </Table>
            )}
          </Panel>
          <Panel title="Recent orders" action={<Link className="link" to="/orders">View all →</Link>}>
            {orders.length === 0 ? <Empty>This bot has not placed any orders yet.</Empty> : (
              <Table cols="1fr 1.2fr 1fr 1fr 1fr" head={["Time (UTC)", "Order", "Qty", "Price", "Status"]}>
                {orders.map(o => (
                  <div className="tr" key={o.id}><span className="mono">{hms(o.time)}</span><span><b>{o.symbol}</b> · {o.side === "buy" ? "Buy" : "Sell"}</span><span>{o.filled} / {o.qty}</span><span className="mono">{o.price ? money(o.price) : "Market"}</span><StatusBadge s={o.status} /></div>
                ))}
              </Table>
            )}
          </Panel>
        </div>
        <div className="cols" style={{ gridTemplateColumns: "1fr" }}>
          <Panel title="Connection">
            <div className="kv" style={{ gridTemplateColumns: "auto 1fr", rowGap: 14 }}>
              <span>Broker account</span><span>{b.broker}</span>
              <span>Mode</span><span className={b.paper ? "acc" : "pos"}>{b.paper ? "Paper trading" : "Live trading"}</span>
              <span>Heartbeat</span><span><span className={"dotl" + (b.online ? " on" : "")} />{b.online ? "Online" : "Offline"}</span>
              <span>API keys</span><span>{keys.length}</span>
            </div>
            {keys.map(k => <div className="row sm muted" key={k.id} style={{ borderTop: "1px solid var(--bd)", marginTop: 14, paddingTop: 12 }}><span>{k.name} · {k.hint}</span><span>{k.lastUsed ? agoMs(k.lastUsed) : "never used"}</span></div>)}
            {keys.length === 0 && <div className="muted sm" style={{ marginTop: 14 }}>No API key yet. <Link className="link" to="/api-keys">Create one →</Link></div>}
          </Panel>
          <Panel title="Open orders" badge={open.length ? <Badge tone="blue">{open.length} OPEN</Badge> : undefined}>
            {open.map(o => (
              <div className="tr tall" key={o.id} style={{ gridTemplateColumns: "1fr auto auto" }}>
                <span><span className="name">{o.symbol}</span><small>{o.side === "buy" ? "Buy" : "Sell"} {o.qty} · {o.type === "limit" ? "Limit" : "Market"} {o.price ? money(o.price) : ""}</small></span>
                <StatusBadge s={o.status} />
                {canTrade && <Confirm onYes={() => api(`/orders/${o.id}/cancel`, "POST").then(reload)}><span className="neg">Cancel</span></Confirm>}
              </div>
            ))}
            {!open.length && <div className="muted sm">No open orders</div>}
          </Panel>
        </div>
      </div>
      {edit && <BotForm bot={b} brokers={brokers} onClose={() => setEdit(false)} onDone={() => { setEdit(false); reload(); }} />}
    </>
  );
}
