import { FormEvent, useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, useApi } from "../api";
import { hms, money, num, pct } from "../format";
import type { BotRow, BrokerRow, Order } from "../types";
import { Confirm, Empty, Field, Head, Metric, Modal, StatusBadge, Table, useDebounced, useMe } from "../ui";

interface Res {
  total: number; items: Order[];
  counts: { all: number; open: number; filled: number; cancelled: number; rejected: number; pending: number; partial: number };
}
const TABS = [["all", "All orders", ""], ["open", "Open", "open"], ["filled", "Filled", "filled"], ["cancelled", "Cancelled", "cancelled"], ["rejected", "Rejected", "rejected"]] as const;
const SIZE = 8;

export function OrderForm({ symbol, onClose, onDone }: { symbol?: string; onClose: () => void; onDone: () => void }) {
  const [brokers] = useApi<BrokerRow[]>("/brokers");
  const [bots] = useApi<{ bots: BotRow[] }>("/bots");
  const [brokerId, setBrokerId] = useState(""), [type, setType] = useState("market"), [err, setErr] = useState(""), [busy, setBusy] = useState(false);
  const [sym, setSym] = useState(symbol ?? ""), [qty, setQty] = useState(""), [limit, setLimit] = useState(""), [side, setSide] = useState("buy");
  const [quote, setQuote] = useState<{ price: number } | null>(null), [quoteErr, setQuoteErr] = useState("");
  const dsym = useDebounced(sym.trim().toUpperCase(), 400);
  useEffect(() => { if (brokers?.length && !brokerId) setBrokerId(brokers[0].id); }, [brokers, brokerId]);
  useEffect(() => {
    setQuote(null); setQuoteErr("");
    if (!dsym || !brokerId) return;
    let live = true;
    api<{ price: number }>(`/quote?symbol=${encodeURIComponent(dsym)}&brokerId=${brokerId}`).then(q => live && setQuote(q), e => live && setQuoteErr((e as Error).message));
    return () => { live = false; };
  }, [dsym, brokerId]);
  if (!brokers || !bots) return null;
  const broker = brokers.find(b => b.id === brokerId);
  const q = Number(qty), lp = Number(limit);
  const unit = type === "limit" && lp > 0 ? lp : quote?.price ?? 0;
  const total = q > 0 ? q * unit : 0;
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setBusy(true); setErr("");
    try {
      await api("/orders", "POST", {
        brokerId, botId: f.get("botId") || null, symbol: f.get("symbol"), side: f.get("side"), type, qty: Number(f.get("qty")),
        limitPrice: type === "limit" ? Number(f.get("limit")) : null, timeInForce: f.get("tif"),
      });
      onDone();
    } catch (x) { setErr((x as Error).message); setBusy(false); }
  }
  if (!brokers.length) return <Modal title="New order" onClose={onClose}><Empty>Connect a broker account first.</Empty></Modal>;
  return (
    <Modal title="New order" onClose={onClose}>
      <form onSubmit={submit} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
        <Field label="Broker account"><select className="inp" value={brokerId} onChange={e => setBrokerId(e.target.value)}>{brokers.map(b => <option key={b.id} value={b.id}>{b.name}{b.paper ? " (paper)" : " (LIVE)"}</option>)}</select></Field>
        <div className="row2">
          <Field label="Symbol"><input className="inp" name="symbol" value={sym} onChange={e => setSym(e.target.value)} required maxLength={15} placeholder="AAPL" style={{ textTransform: "uppercase" }} autoFocus /></Field>
          <Field label="Side"><select className="inp" name="side" value={side} onChange={e => setSide(e.target.value)}><option value="buy">Buy</option><option value="sell">Sell</option></select></Field>
        </div>
        <div className="row2">
          <Field label="Type"><select className="inp" value={type} onChange={e => setType(e.target.value)}><option value="market">Market</option><option value="limit">Limit</option></select></Field>
          <Field label="Quantity"><input className="inp" name="qty" type="number" min="0" step="any" required value={qty} onChange={e => setQty(e.target.value)} /></Field>
        </div>
        <div className="row2">
          <Field label="Limit price (USD)"><input className="inp" name="limit" type="number" min="0" step="any" disabled={type !== "limit"} required={type === "limit"} value={limit} onChange={e => setLimit(e.target.value)} /></Field>
          <Field label="Time in force"><select className="inp" name="tif"><option value="day">Day</option><option value="gtc">Good till cancelled</option></select></Field>
        </div>
        <Field label="Attribute to bot (optional)">
          <select className="inp" name="botId"><option value="">Manual order</option>{bots.bots.filter(b => b.brokerId === brokerId).map(b => <option key={b.id} value={b.id}>{b.name}</option>)}</select>
        </Field>
        <div className="estimate">
          <div className="row"><span className="muted">Latest price{dsym ? ` · ${dsym}` : ""}</span><b>{quote ? money(quote.price) : quoteErr ? <span className="neg">{quoteErr}</span> : dsym ? "…" : "—"}</b></div>
          <div className="row"><span className="muted">{side === "buy" ? "Estimated cost" : "Estimated proceeds"}{q > 0 ? ` · ${q} × ${unit ? money(unit) : "—"}` : ""}</span><b style={{ fontSize: 18 }}>{total > 0 ? money(total) : "—"}</b></div>
          {side === "buy" && total > 0 && broker && total > broker.buyingPower && <div className="warn">This is more than the buying power of {broker.name} ({money(broker.buyingPower)}). The broker will probably reject it.</div>}
          {type === "market" && <div className="muted sm">Market orders fill at the market price, which can differ from the latest price shown.</div>}
        </div>
        {err && <div className="err" role="alert">{err}</div>}
        <div className="actions"><button type="button" className="btn" onClick={onClose}>Cancel</button><button className="btn pri" disabled={busy}>{total > 0 ? `${side === "buy" ? "Buy" : "Sell"} · ${money(total)}` : "Place order"}</button></div>
      </form>
    </Modal>
  );
}

export function Orders() {
  const { canTrade } = useMe();
  const [sp, setSp] = useSearchParams();
  const q = sp.get("q") ?? "", prefill = sp.get("new");
  const [tab, setTab] = useState(""), [page, setPage] = useState(1), [form, setForm] = useState(prefill !== null);
  const [d, reload] = useApi<Res>(`/orders?size=${SIZE}&page=${page}&status=${tab}&q=${encodeURIComponent(q)}`, 5000);
  if (!d) return null;
  const { counts: c, items, total } = d;
  const pages = Math.max(1, Math.ceil(total / SIZE));
  const setFilter = (t: string) => { setTab(t); setPage(1); };
  const cancel = (id: string) => api(`/orders/${id}/cancel`, "POST").then(reload);
  return (
    <>
      <Head title="Orders" sub="Track execution across every strategy and broker.">
        <a className="btn" href={`/api/orders/export.csv?status=${tab}&q=${encodeURIComponent(q)}`}>↓ Export CSV</a>
        {canTrade && <button className="btn" onClick={async () => { if (window.confirm("Cancel all open orders on every broker account?")) { try { await api("/orders/cancel-open", "POST"); reload(); } catch (e) { window.alert((e as Error).message); } } }}>Cancel open orders</button>}
        {canTrade && <button className="btn pri" onClick={() => setForm(true)}>+ New order</button>}
      </Head>
      <div className="grid4">
        <Metric label="Total orders" value={num(c.all)} sub="Synced from your broker accounts" />
        <Metric label="Filled orders" value={num(c.filled)} sub={`${c.all ? pct(c.filled / c.all) : "0.00%"} of all orders`} tone="pos" />
        <Metric label="Open orders" value={c.open} sub={<><span className="acc">{c.pending} pending</span> · <span className="acc">{c.partial} partially filled</span></>} />
        <Metric label="Cancelled / Rejected" value={`${c.cancelled} / ${c.rejected}`} sub="Closed without a full fill" />
      </div>
      <section className="card">
        <div className="tabs">
          {TABS.map(([k, label, f]) => <button key={k} className={(tab === f ? "on" : "")} onClick={() => setFilter(f)}>{label}<span>{num(c[k])}</span></button>)}
        </div>
        <div className="tools">
          <input className="inp" placeholder="Search order ID or symbol…" defaultValue={q} maxLength={40} aria-label="Search orders"
            onKeyDown={e => { if (e.key === "Enter") { setPage(1); setSp(e.currentTarget.value ? { q: e.currentTarget.value } : {}); } }} />
          <span className="pos sm" style={{ marginLeft: "auto" }}>↻ Auto-refresh on</span>
        </div>
        <div style={{ padding: "0 20px 16px" }}>
          {items.length === 0 ? <Empty>No orders found.</Empty> : (
            <Table cols="1.5fr .9fr .8fr .9fr 1.5fr 1.1fr 1fr .9fr .7fr" head={["Order / Instrument", "Side / Type", "Filled / Qty", "Price", "Strategy", "Broker / Mode", "Status", "Time (UTC)", "Action"]}>
              {items.map(o => (
                <div className="tr tall" key={o.id}>
                  <span style={{ display: "flex", gap: 18 }}><span className="muted sm">{o.id}</span><b>{o.symbol}</b></span>
                  <span className={o.side === "buy" ? "pos" : "neg"}>{o.side === "buy" ? "Buy" : "Sell"}<small>{o.type === "limit" ? "Limit" : "Market"}</small></span>
                  <span className="mono">{o.filled} / {o.qty}</span><span className="mono">{o.price ? money(o.price) : "Market"}</span>
                  <span>{o.strategy}</span><span className="muted">{o.mode}</span>
                  <StatusBadge s={o.status} /><span className="mono muted">{hms(o.time)}</span>
                  {(o.status === "pending" || o.status === "partial") && canTrade ? <Confirm onYes={() => cancel(o.id)}><span className="neg">Cancel</span></Confirm>
                    : o.botId ? <Link className="link" to={`/bots/${o.botId}`}>Details</Link> : <span />}
                </div>
              ))}
            </Table>
          )}
          <div className="pager">
            <span>Showing {total ? (page - 1) * SIZE + 1 : 0}–{Math.min(page * SIZE, total)} of {num(total)} orders</span>
            <span />
            <div><button className="btn" disabled={page <= 1} onClick={() => setPage(page - 1)}>‹ Previous</button><button className="btn" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next ›</button></div>
          </div>
        </div>
      </section>
      {form && <OrderForm symbol={prefill ?? undefined} onClose={() => { setForm(false); if (prefill !== null) setSp({}); }} onDone={() => { setForm(false); setSp({}); reload(); }} />}
    </>
  );
}
