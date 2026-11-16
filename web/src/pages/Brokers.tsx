import { FormEvent, useState } from "react";
import { api, useApi } from "../api";
import { agoMs, hms, money } from "../format";
import type { BrokerRow } from "../types";
import { Badge, Confirm, Empty, Field, Head, Metric, Modal, Panel, PasswordInput, Table, useMe } from "../ui";

interface Ev { time: number; broker: string; event: string; ok: boolean }

function BrokerForm({ broker, onClose, onDone }: { broker?: BrokerRow; onClose: () => void; onDone: () => void }) {
  const [err, setErr] = useState(""), [busy, setBusy] = useState(false);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const body = { name: f.get("name"), keyId: f.get("keyId"), secret: f.get("secret"), paper: f.get("env") !== "live" };
    setBusy(true); setErr("");
    try { await (broker ? api(`/brokers/${broker.id}`, "PUT", body) : api("/brokers", "POST", body)); onDone(); }
    catch (x) { setErr((x as Error).message); setBusy(false); }
  }
  return (
    <Modal title={broker ? "Update broker" : "Connect Alpaca account"} onClose={onClose}>
      <form onSubmit={submit} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
        <Field label="Display name"><input className="inp" name="name" defaultValue={broker?.name ?? "Alpaca Paper"} required maxLength={60} autoFocus /></Field>
        <Field label="API key ID"><input className="inp" name="keyId" autoComplete="off" spellCheck={false} required={!broker} placeholder={broker ? "Leave empty to keep the current key" : "PK…"} maxLength={64} /></Field>
        <Field label="API secret"><PasswordInput name="secret" autoComplete="off" required={!broker} /></Field>
        {!broker && (
          <Field label="Environment" hint="Live accounts must be enabled on the server (Brokers:AllowLive).">
            <select className="inp" name="env"><option value="paper">Paper trading</option><option value="live">Live trading</option></select>
          </Field>
        )}
        <p className="muted sm">Keys are checked against Alpaca before saving, then stored encrypted on the server. Create paper keys at app.alpaca.markets → Paper Trading → API Keys.</p>
        {err && <div className="err" role="alert">{err}</div>}
        <div className="actions"><button type="button" className="btn" onClick={onClose}>Cancel</button><button className="btn pri" disabled={busy}>{busy ? "Checking…" : broker ? "Save" : "Connect"}</button></div>
      </form>
    </Modal>
  );
}

export function Brokers() {
  const { isAdmin, canTrade } = useMe();
  const [accs, reload] = useApi<BrokerRow[]>("/brokers", 10000);
  const [ev, reloadEv] = useApi<Ev[]>("/brokers/events", 5000);
  const [form, setForm] = useState<BrokerRow | "new" | null>(null);
  const [lat, setLat] = useState<Record<string, number>>({});
  if (!accs || !ev) return null;
  const ok = accs.filter(a => a.connected).length;
  const refresh = () => { reload(); reloadEv(); };
  const test = async (a: BrokerRow) => {
    const r = await api<{ ok: boolean; latencyMs?: number; error?: string }>(`/brokers/${a.id}/test`, "POST");
    if (r.ok) setLat(l => ({ ...l, [a.id]: r.latencyMs ?? 0 })); else window.alert(r.error);
    refresh();
  };
  const sync = async () => { await Promise.all(accs.map(a => api(`/brokers/${a.id}/sync`, "POST"))); refresh(); };
  return (
    <>
      <Head title="Brokers" sub="Your connected accounts, balances and execution gateways.">
        {canTrade && <button className="btn" onClick={() => sync().catch(e => window.alert(e.message))} disabled={!accs.length}>⟳ Sync accounts</button>}
        {isAdmin && <button className="btn pri" onClick={() => setForm("new")}>+ Connect broker</button>}
      </Head>
      <div className="grid4">
        <Metric label="Total equity" value={money(accs.reduce((s, a) => s + a.equity, 0))} sub={`Across ${accs.length} connected account${accs.length === 1 ? "" : "s"}`} />
        <Metric label="Available cash" value={money(accs.reduce((s, a) => s + a.cash, 0))} sub="Before orders in flight" />
        <Metric label="Connection health" value={`${ok} / ${accs.length} healthy`} sub={ok === accs.length ? "All broker connections operational" : "Some connections need attention"} tone={ok === accs.length ? "pos" : "neg"} />
        <Metric label="Buying power" value={money(accs.reduce((s, a) => s + a.buyingPower, 0))} sub="As reported by the broker" />
      </div>
      {accs.length === 0 ? (
        <section className="card panel"><Empty>No broker connected yet.{isAdmin && <><br /><br /><button className="btn pri" onClick={() => setForm("new")}>Connect your Alpaca account</button></>}</Empty></section>
      ) : (
        <div className="grid4" style={{ gridTemplateColumns: "repeat(3,1fr)" }}>
          {accs.map(a => (
            <div className="card panel" key={a.id} style={{ borderTop: `2px solid ${a.connected ? (a.paper ? "var(--ac)" : "var(--gr)") : "var(--rd)"}` }}>
              <div className="row"><h2>{a.name}</h2><Badge tone={a.paper ? "blue" : "green"}>{a.paper ? "PAPER" : "LIVE"}</Badge></div>
              <div className="muted sm" style={{ margin: "4px 0 18px" }}>{a.accountNumber ? `Account ${a.accountNumber} · ` : ""}{a.mask}</div>
              <div className="muted sm">Equity</div>
              <div style={{ fontSize: 26, fontWeight: 600, margin: "6px 0 10px" }}>{money(a.equity)}</div>
              <div className="row sm" style={{ paddingBottom: 14, borderBottom: "1px solid var(--bd)" }}>
                {a.connected ? <span className="pos">● Connected</span> : <span className="neg">● {a.error ?? "Not connected"}</span>}
                <span className="muted">{lat[a.id] ?? a.latencyMs} ms latency</span>
              </div>
              <div className="kv" style={{ gridTemplateColumns: "1fr 1fr", margin: "14px 0" }}>
                <span>Available cash</span><span>{money(a.cash)}</span><span>Buying power</span><span>{money(a.buyingPower)}</span>
                <span>Assigned bots</span><span>{a.bots}</span><span>Last order sync</span><span>{a.lastSync ? agoMs(a.lastSync) : "—"}</span>
              </div>
              <div className="row" style={{ borderTop: "1px solid var(--bd)", paddingTop: 14 }}>
                {isAdmin ? <button className="link" onClick={() => setForm(a)}>Update keys</button> : <span />}
                <span className="actions" style={{ gap: 16 }}>
                  {canTrade && <Confirm onYes={() => test(a)}><span className="muted">Test connection</span></Confirm>}
                  {isAdmin && <Confirm onYes={async () => { if (window.confirm(`Remove "${a.name}" and its order history from OpenTrade? Your Alpaca account is not affected.`)) { await api(`/brokers/${a.id}`, "DELETE"); refresh(); } }}><span className="neg">Remove</span></Confirm>}
                </span>
              </div>
            </div>
          ))}
        </div>
      )}
      <Panel title="Connection activity" note="Latest events since the server started">
        {ev.length === 0 ? <Empty>No activity yet.</Empty> : (
          <Table cols="1fr 1.5fr 3fr 1fr" head={["Time (UTC)", "Broker", "Event", "Status"]}>
            {ev.map((e, i) => <div className="tr" key={i}><span className="mono">{hms(e.time)}</span><span>{e.broker}</span><span>{e.event}</span><span><Badge tone={e.ok ? "green" : "red"}>{e.ok ? "Success" : "Failed"}</Badge></span></div>)}
          </Table>
        )}
      </Panel>
      {form && <BrokerForm broker={form === "new" ? undefined : form} onClose={() => setForm(null)} onDone={() => { setForm(null); refresh(); }} />}
    </>
  );
}
