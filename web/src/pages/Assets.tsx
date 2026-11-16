import { useState } from "react";
import { Link } from "react-router-dom";
import { useApi } from "../api";
import { num } from "../format";
import { Badge, Empty, Head, Panel, Table, useDebounced, useMe } from "../ui";

interface Asset { symbol: string; name: string; exchange: string; class: string; shortable: boolean; fractionable: boolean }
interface Res { total: number; page: number; pages: number; items: Asset[] }

export function Assets() {
  const { canTrade } = useMe();
  const [q, setQ] = useState(""), [cls, setCls] = useState(""), [page, setPage] = useState(1);
  const dq = useDebounced(q);
  const [d] = useApi<Res>(`/assets?size=15&page=${page}&cls=${cls}&q=${encodeURIComponent(dq)}`);
  const [err] = useApi<{ error?: string }>("/brokers");
  return (
    <>
      <Head title="Assets" sub="Every instrument your broker lets you trade." />
      <Panel title="Tradable assets" note={d ? `${num(d.total)} matching` : undefined}>
        <div className="tools" style={{ padding: "0 0 14px" }}>
          <input className="inp" style={{ flex: "0 0 240px" }} placeholder="Search symbol or name…" value={q} maxLength={40} aria-label="Search assets" onChange={e => { setQ(e.target.value); setPage(1); }} />
          <select className="inp" value={cls} onChange={e => { setCls(e.target.value); setPage(1); }}><option value="">All classes</option><option value="us_equity">US equities</option><option value="crypto">Crypto</option></select>
        </div>
        {!d ? <Empty>{Array.isArray(err) && err.length === 0 ? <>Connect a broker to browse assets. <Link className="link" to="/brokers">Brokers →</Link></> : "Loading…"}</Empty>
          : d.items.length === 0 ? <Empty>No assets match your search.</Empty> : (
            <Table cols="1fr 3fr 1fr 1fr 1.4fr 1fr" head={["Symbol", "Name", "Exchange", "Class", "Features", ""]}>
              {d.items.map(a => (
                <div className="tr" key={a.symbol}>
                  <b>{a.symbol}</b><span>{a.name}</span><span className="muted">{a.exchange}</span><span>{a.class === "us_equity" ? "US equity" : a.class}</span>
                  <span className="actions" style={{ justifyContent: "flex-start", gap: 6 }}>{a.fractionable && <Badge tone="blue">FRACTIONAL</Badge>}{a.shortable && <Badge tone="grey">SHORTABLE</Badge>}</span>
                  {canTrade ? <Link className="link" to={`/orders?new=${encodeURIComponent(a.symbol)}`}>Trade →</Link> : <span />}
                </div>
              ))}
            </Table>
          )}
        {d && (
          <div className="pager">
            <span>Page {d.page} of {num(d.pages)}</span><span />
            <div><button className="btn" disabled={d.page <= 1} onClick={() => setPage(d.page - 1)}>‹ Previous</button><button className="btn" disabled={d.page >= d.pages} onClick={() => setPage(d.page + 1)}>Next ›</button></div>
          </div>
        )}
      </Panel>
    </>
  );
}
