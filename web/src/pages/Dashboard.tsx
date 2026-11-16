import { useState } from "react";
import { Link } from "react-router-dom";
import { useApi } from "../api";
import { hms, money, money0, pct, signed, spct, tone } from "../format";
import type { Order, Series } from "../types";
import { Badge, Empty, Head, LineChart, Metric, Panel, Range, StatusBadge, Table } from "../ui";

export const COLORS = ["var(--ac)", "var(--pu)", "var(--te)", "var(--am)", "var(--sl)", "var(--gr)"];

export function TxRow({ o }: { o: Order }) {
  return (
    <div className="tr tall">
      <span className="mono">{hms(o.time)}</span>
      <span><b>{o.symbol}</b> · {o.side === "buy" ? "Buy" : "Sell"}</span>
      <span>{o.strategy}</span><span>{o.broker}</span><span>{o.qty} shares</span>
      <span className={"mono " + (o.amount > 0 ? "pos" : "")}>{o.amount > 0 ? "+" : "−"}{money(Math.abs(o.amount))}</span>
      <StatusBadge s={o.status} />
    </div>
  );
}

export interface Dash {
  brokers: number; connected: number; anyLive: boolean; errors: { broker: string; error: string }[];
  nav: number; day: number; dayPct: number; month: number; monthPct: number; total: number; totalPct: number; cash: number; cashPct: number;
  activeBots: number; totalBots: number; openOrders: number; positions: number;
  assetClasses: { name: string; pct: number }[];
  bots: { id: string; name: string; allocated: number }[]; orders: Order[];
}

export function Dashboard() {
  const [d] = useApi<Dash>("/dashboard", 10000);
  const [range, setRange] = useState("1M");
  const [s] = useApi<Series>("/series/portfolio?range=" + range, 60000);
  if (!d) return null;
  if (d.brokers === 0) {
    return (
      <>
        <Head title="Dashboard" sub="Your capital, strategies and execution at a glance." />
        <section className="card panel"><Empty>No broker is connected yet.<br /><br /><Link className="btn pri" to="/brokers">Connect your Alpaca account</Link></Empty></section>
      </>
    );
  }
  const deployed = d.bots.reduce((a, b) => a + b.allocated, 0);
  return (
    <>
      <Head title="Dashboard" badge={<Badge tone={d.anyLive ? "green" : "blue"}>{d.anyLive ? "LIVE ACCOUNTS" : "PAPER ACCOUNTS"}</Badge>} sub="Your capital, strategies and execution at a glance." />
      {d.errors.map(e => <div className="banner" key={e.broker}><Badge tone="red">ERROR</Badge><span>{e.broker}: {e.error}</span><Link className="link" to="/brokers">Fix connection →</Link></div>)}
      <div className="grid4">
        <Metric label="Total portfolio value" value={money(d.nav)} sub={`Across ${d.connected} connected account${d.connected === 1 ? "" : "s"}`} />
        <Metric label="Daily P&L" value={signed(d.day)} sub={`${d.day < 0 ? "↓" : "↑"} ${pct(Math.abs(d.dayPct))}  vs. previous close`} tone={tone(d.day)} />
        <Metric label="P&L · past year" value={signed(d.total)} sub={`${d.total < 0 ? "↓" : "↑"} ${pct(Math.abs(d.totalPct))}  over the account history`} tone={tone(d.total)} />
        <Metric label="Available cash" value={money(d.cash)} sub={`${pct(d.cashPct)} of portfolio`} />
      </div>
      <div className="cols" style={{ gridTemplateColumns: "1fr 310px" }}>
        <Panel title="Portfolio performance" note="Equity curve · all connected accounts" action={<Range ranges={["1D", "1W", "1M", "3M", "YTD", "ALL"]} value={range} onChange={setRange} />}>
          <div className="row" style={{ marginBottom: 6 }}>
            <span style={{ fontSize: 22, fontWeight: 600 }}>{money(d.nav)}</span>
            {s && s.v.length > 1 && <span className={"sm " + tone(s.change ?? 0)}>{signed(s.change ?? 0)} ({spct(s.pct).slice(1)}) this {range === "1D" ? "day" : "period"}</span>}
          </div>
          {s && <LineChart s={s} fmt={v => v >= 1e6 ? `$${(v / 1e6).toFixed(2)}M` : `$${(v / 1e3).toFixed(1)}k`} hours={range === "1D"} />}
        </Panel>
        <Panel title="Capital allocation" note={d.bots.length ? `${money0(deployed)} allocated across ${d.bots.length} bots` : "Capital assigned to your bots"}>
          {d.bots.length === 0 ? <Empty>No capital allocated yet.</Empty> : (
            <>
              <div className="seg">{d.bots.map((b, i) => <i key={b.id} style={{ flex: b.allocated, background: COLORS[i % COLORS.length] }} />)}</div>
              <div className="list">
                {d.bots.map((b, i) => (
                  <div className="row" key={b.id}>
                    <span><span className="dot" style={{ background: COLORS[i % COLORS.length] }} />{b.name}</span>
                    <span><b style={{ marginRight: 28 }}>{money0(b.allocated)}</b><span className="muted">{Math.round((b.allocated / deployed) * 100)}%</span></span>
                  </div>
                ))}
              </div>
            </>
          )}
          <div className="row" style={{ borderTop: "1px solid var(--bd)", marginTop: 20, paddingTop: 14 }}>
            <Link className="link" to="/bots">Manage allocations</Link><span className="acc">→</span>
          </div>
        </Panel>
      </div>
      <Panel title="Recent transactions" note="Latest filled orders" action={<Link className="link" to="/orders">View all orders →</Link>}>
        {d.orders.length === 0 ? <Empty>No filled orders yet.</Empty> : (
          <Table cols="1fr 1.1fr 1.5fr 1.4fr 1fr 1.2fr 1fr" head={["Time (UTC)", "Asset / action", "Strategy", "Broker", "Quantity", "Amount", "Status"]}>
            {d.orders.map(o => <TxRow key={o.id} o={o} />)}
          </Table>
        )}
      </Panel>
    </>
  );
}
