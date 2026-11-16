import { Link } from "react-router-dom";
import { api, useApi } from "../api";
import { money, num, signed, spct, tone } from "../format";
import { Badge, Confirm, Empty, Head, Metric, Panel, Table, useMe } from "../ui";

interface Row { brokerId: string; broker: string; paper: boolean; symbol: string; qty: number; avg: number; last: number; value: number; pnl: number; pnlPct: number; cls: string }
interface Res { items: Row[]; errors: { broker: string; error: string }[]; totals: { value: number; pnl: number; count: number; winners: number; losers: number } }

export function Positions() {
  const { canTrade } = useMe();
  const [d, reload] = useApi<Res>("/positions", 10000);
  if (!d) return null;
  const t = d.totals;
  return (
    <>
      <Head title="Positions" sub="Everything you currently hold across your broker accounts.">
        {canTrade && <Link className="btn pri" to="/orders?new=">+ New order</Link>}
      </Head>
      {d.errors.map(e => <div className="banner" key={e.broker}><Badge tone="red">ERROR</Badge><span>{e.broker}: {e.error}</span></div>)}
      <div className="grid4">
        <Metric label="Market value" value={money(t.value)} sub="Long and short positions" />
        <Metric label="Unrealized P&L" value={signed(t.pnl)} sub="Open positions only" tone={tone(t.pnl)} />
        <Metric label="Open positions" value={num(t.count)} sub="Across all accounts" />
        <Metric label="Winners / losers" value={`${t.winners} / ${t.losers}`} sub="By unrealized P&L" />
      </div>
      <Panel title="Open positions" action={<span className="pos sm">↻ Auto-refresh on</span>}>
        {d.items.length === 0 ? <Empty>No open positions.</Empty> : (
          <Table cols="1.1fr 1.4fr .9fr 1fr 1fr 1.2fr 1.4fr 1fr" head={["Instrument", "Account", "Quantity", "Avg. entry", "Last price", "Market value", "Unrealized P&L", "Actions"]}>
            {d.items.map(p => (
              <div className="tr tall" key={p.brokerId + p.symbol}>
                <span><b>{p.symbol}</b><small>{p.cls}</small></span>
                <span>{p.broker} {p.paper && <Badge tone="blue">PAPER</Badge>}</span>
                <span className="mono">{p.qty}</span><span className="mono">{money(p.avg)}</span><span className="mono">{money(p.last)}</span><span className="mono">{money(p.value)}</span>
                <span className={tone(p.pnl)}>{signed(p.pnl)}<small>{spct(p.pnlPct)}</small></span>
                <span className="actions" style={{ justifyContent: "flex-start", gap: 14 }}>
                  {canTrade && <Link className="link" to={`/orders?new=${encodeURIComponent(p.symbol)}`}>Trade</Link>}
                  {canTrade && <Confirm onYes={async () => { if (window.confirm(`Close the whole ${p.symbol} position at market?`)) { await api("/positions/close", "POST", { brokerId: p.brokerId, symbol: p.symbol }); reload(); } }}><span className="neg">Close</span></Confirm>}
                </span>
              </div>
            ))}
          </Table>
        )}
      </Panel>
    </>
  );
}
