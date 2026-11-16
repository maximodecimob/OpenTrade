import { Link } from "react-router-dom";
import { useApi } from "../api";
import { money, pct, signed, spct, tone } from "../format";
import type { BotRow } from "../types";
import { Badge, Donut, Empty, Head, Metric, Panel, Progress, Table } from "../ui";
import { COLORS, type Dash } from "./Dashboard";

interface P { id: string; name: string; sub: string; paper: boolean; error: string | null; nav: number; deployed: number; cash: number; dailyPnl: number; positions: number }

export function Portfolios() {
  const [p] = useApi<{ portfolios: P[]; nav: number }>("/portfolios", 10000);
  const [b] = useApi<{ bots: BotRow[] }>("/bots", 10000);
  const [d] = useApi<Dash>("/dashboard", 10000);
  if (!p || !b || !d) return null;
  const deployed = p.portfolios.reduce((a, x) => a + x.deployed, 0), cash = p.portfolios.reduce((a, x) => a + x.cash, 0);
  const day = p.portfolios.reduce((a, x) => a + x.dailyPnl, 0);
  const allocated = b.bots.reduce((a, x) => a + x.allocated, 0);
  if (!p.portfolios.length) {
    return (<><Head title="Portfolios" sub="One portfolio per connected broker account." /><section className="card panel"><Empty>No broker is connected yet.<br /><br /><Link className="btn pri" to="/brokers">Connect your Alpaca account</Link></Empty></section></>);
  }
  return (
    <>
      <Head title="Portfolios" sub="One portfolio per connected broker account." />
      <div className="grid4">
        <Metric label="Net asset value" value={money(p.nav)} sub={`${p.portfolios.length} portfolio${p.portfolios.length === 1 ? "" : "s"}`} />
        <Metric label="Deployed capital" value={money(deployed)} sub={`${p.nav ? pct(deployed / p.nav) : "0.00%"} of total portfolio value`} />
        <Metric label="Available cash" value={money(cash)} sub="Ready to trade" />
        <Metric label="Daily P&L" value={signed(day)} sub={`${p.nav - day ? spct(day / (p.nav - day)) : "0.00%"} vs. previous close`} tone={tone(day)} />
      </div>
      <Panel title="Your portfolios">
        <Table cols="1.7fr 1.2fr 1.1fr 1.2fr 1.2fr 1.5fr" head={["Portfolio", "Net asset value", "Daily P&L", "Deployed", "Available cash", "Allocation"]}>
          {p.portfolios.map(x => (
            <div className="tr tall" key={x.id}>
              <span><span className="name">{x.name}</span><small>{x.error ? <span className="neg">{x.error}</span> : x.sub}</small></span>
              <b>{money(x.nav)}</b>
              <span className={tone(x.dailyPnl)}>{signed(x.dailyPnl)}<small>{x.nav ? spct(x.dailyPnl / (x.nav - x.dailyPnl || 1)) : "—"}</small></span>
              <span>{money(x.deployed)}</span><span>{money(x.cash)}</span>
              <span>{p.nav ? pct(x.nav / p.nav) : "—"}<Progress value={p.nav ? x.nav / p.nav : 0} thin /></span>
            </div>
          ))}
        </Table>
      </Panel>
      <div className="cols" style={{ gridTemplateColumns: "1fr 1fr" }}>
        <Panel title="Asset allocation" note="Market value by asset class">
          {d.assetClasses.length === 0 ? <Empty>No positions yet.</Empty> : (
            <div className="row" style={{ gap: 24, justifyContent: "flex-start", margin: "10px 0 6px" }}>
              <Donut parts={d.assetClasses.map(c => c.pct)} colors={COLORS} center={String(d.positions)} sub="positions" />
              <div className="list" style={{ flex: 1, gap: 12 }}>
                {d.assetClasses.map((c, i) => <div className="row" key={c.name}><span><span className="dot" style={{ background: COLORS[i % COLORS.length] }} />{c.name}</span><span>{c.pct.toFixed(2)}%</span></div>)}
              </div>
            </div>
          )}
        </Panel>
        <Panel title="Strategy distribution" note="Share of capital allocated to bots" action={<Badge tone="blue">{b.bots.length} BOTS</Badge>}>
          {allocated === 0 ? <Empty>No capital allocated to bots yet.</Empty> : (
            <div className="list" style={{ gap: 14, marginTop: 6 }}>
              {b.bots.filter(x => x.allocated > 0).map((x, i) => (
                <div key={x.id}><div className="row" style={{ marginBottom: 6 }}><span>{x.name}</span><span>{Math.round((x.allocated / allocated) * 100)}%</span></div><Progress value={x.allocated / allocated} color={COLORS[i % COLORS.length]} thin /></div>
              ))}
            </div>
          )}
          <Link className="link" style={{ display: "inline-block", marginTop: 18 }} to="/bots">Manage strategy allocations →</Link>
        </Panel>
      </div>
    </>
  );
}
