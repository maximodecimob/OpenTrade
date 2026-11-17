import { createContext, ReactNode, useContext, useEffect, useState } from "react";
import { mon } from "./format";
import type { Series } from "./types";

export const Icon = ({ n, className = "" }: { n: string; className?: string }) => (
  <i className={"ic " + className} style={{ ["--i" as string]: `url(/icons/${n}.svg)` }} />
);

export const Badge = ({ tone, children }: { tone: string; children: ReactNode }) => <span className={"badge b-" + tone}>{children}</span>;

const STATUS: Record<string, [string, string]> = {
  filled: ["green", "Filled"], pending: ["blue", "Pending"], partial: ["amber", "Partial fill"], cancelled: ["grey", "Cancelled"], rejected: ["red", "Rejected"],
  running: ["green", "Running"], paused: ["amber", "Paused"], simulated: ["blue", "Paper"],
};
export const StatusBadge = ({ s }: { s: string }) => <Badge tone={(STATUS[s] ?? ["grey", s])[0]}>{(STATUS[s] ?? ["grey", s])[1]}</Badge>;

export const Metric = ({ label, value, sub, tone = "" }: { label: string; value: ReactNode; sub?: ReactNode; tone?: string }) => (
  <div className="card metric">
    <div className="muted">{label}</div>
    <div className="mval">{value}</div>
    {sub != null && <div className={"msub " + tone}>{sub}</div>}
  </div>
);

export const Panel = ({ title, note, badge, action, className = "", children }:
  { title: string; note?: string; badge?: ReactNode; action?: ReactNode; className?: string; children: ReactNode }) => (
  <section className={"card panel " + className}>
    <header className="ph">
      <div><h2>{title}{badge}</h2>{note && <div className="muted sm">{note}</div>}</div>
      {action}
    </header>
    {children}
  </section>
);

export const Head = ({ title, sub, badge, children }: { title: string; sub: ReactNode; badge?: ReactNode; children?: ReactNode }) => (
  <div className="head">
    <div><h1>{title}{badge}</h1><p className="muted">{sub}</p></div>
    <div className="actions">{children}</div>
  </div>
);

export const Range = ({ ranges, value, onChange }: { ranges: string[]; value: string; onChange: (r: string) => void }) => (
  <div className="range">{ranges.map(r => <button key={r} className={r === value ? "on" : ""} onClick={() => onChange(r)}>{r}</button>)}</div>
);

export const Progress = ({ value, color = "var(--accent)", thin }: { value: number; color?: string; thin?: boolean }) => (
  <div className={"bar" + (thin ? " thin" : "")}><i style={{ width: Math.max(0, Math.min(100, value * 100)) + "%", background: color }} /></div>
);

export const Table = ({ head, children, cols }: { head: string[]; children: ReactNode; cols: string }) => (
  <div className="tbl" style={{ ["--cols" as string]: cols }}>
    <div className="tr th">{head.map(h => <span key={h}>{h}</span>)}</div>
    {children}
  </div>
);

export const Sel = ({ children }: { children: ReactNode }) => <button className="btn sel">{children}<Icon n="chevron" className="sm-ic" /></button>;

export function Confirm({ onYes, children }: { onYes: () => unknown; children: ReactNode }) {
  const [busy, set] = useState(false);
  return <button className="link" disabled={busy} onClick={async () => {
    set(true);
    try { await onYes(); } catch (e) { window.alert((e as Error).message); } finally { set(false); }
  }}>{children}</button>;
}

export interface Me { id: string; name: string; email: string; role: "admin" | "trader" | "viewer" }
export const UserCtx = createContext<Me>({ id: "", name: "", email: "", role: "viewer" });
export const useMe = () => {
  const u = useContext(UserCtx);
  return { ...u, isAdmin: u.role === "admin", canTrade: u.role !== "viewer" };
};

export function useDebounced<T>(v: T, ms = 300) {
  const [d, set] = useState(v);
  useEffect(() => { const id = setTimeout(() => set(v), ms); return () => clearTimeout(id); }, [v, ms]);
  return d;
}

export const Modal = ({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) => (
  <div className="modal" onMouseDown={e => { if (e.target === e.currentTarget) onClose(); }}>
    <div className="card" role="dialog" aria-label={title} style={wide ? { width: 560 } : undefined}><h2>{title}</h2>{children}</div>
  </div>
);

export const Empty = ({ children }: { children: ReactNode }) => <div className="empty">{children}</div>;

export const Field = ({ label, children, hint }: { label: string; children: ReactNode; hint?: string }) => (
  <label className="fld">{label}{children}{hint && <small className="muted">{hint}</small>}</label>
);

export function PasswordInput(props: { name?: string; value?: string; onChange?: (v: string) => void; autoComplete?: string; required?: boolean; autoFocus?: boolean }) {
  const [show, setShow] = useState(false);
  return (
    <span className="pw">
      <input className="inp" name={props.name} type={show ? "text" : "password"} autoComplete={props.autoComplete} required={props.required} autoFocus={props.autoFocus} maxLength={128}
        value={props.value} onChange={props.onChange ? e => props.onChange!(e.target.value) : undefined} />
      <button type="button" className="eye" onClick={() => setShow(v => !v)} aria-label={show ? "Hide password" : "Show password"} aria-pressed={show}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z" /><circle cx="12" cy="12" r="3" />{show && <path d="M3 3l18 18" />}
        </svg>
      </button>
    </span>
  );
}

/* ---------- SVG charts ---------- */
const W = 760, H = 200, PL = 8, PR = 56, PT = 10, PB = 28;

function niceTicks(min: number, max: number, n = 4) {
  const step0 = (max - min) / (n - 1) || 1, mag = 10 ** Math.floor(Math.log10(step0));
  const step = [1, 2, 2.5, 5, 10].map(m => m * mag).find(s => s >= step0)!;
  const lo = Math.floor(min / step) * step;
  return Array.from({ length: Math.ceil((max - lo) / step) + 1 }, (_, i) => lo + i * step);
}

export function LineChart({ s, fmt, hours }: { s: Series; fmt: (v: number) => string; hours?: boolean }) {
  if (s.v.length < 2) return <div className="empty">Not enough history yet</div>;
  const all = s.bench ? [...s.v, ...s.bench] : s.v;
  const ticks = niceTicks(Math.min(...all), Math.max(...all));
  const lo = ticks[0], hi = ticks[ticks.length - 1];
  const t0 = s.t[0], span = Math.max(1, s.t[s.t.length - 1] - t0);
  const xt = (ms: number) => PL + ((ms - t0) / span) * (W - PL - PR);
  const x = (i: number) => xt(s.t[i]);
  const y = (v: number) => PT + (1 - (v - lo) / (hi - lo)) * (H - PT - PB);
  const path = (a: number[]) => a.map((v, i) => `${i ? "L" : "M"}${x(i).toFixed(1)} ${y(v).toFixed(1)}`).join("");
  const label = (ms: number) => (hours ? new Date(ms).toISOString().slice(11, 16) : mon(ms));
  // Five evenly spaced moments in real time (not by point index), so market closes and weekends do not distort the axis.
  const xl = [0, 1, 2, 3, 4].map(k => t0 + (k / 4) * span).filter((ms, k, arr) => k === 0 || label(ms) !== label(arr[k - 1]));
  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="chart" role="img">
      <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#38bdf8" stopOpacity=".28" /><stop offset="1" stopColor="#38bdf8" stopOpacity="0" /></linearGradient></defs>
      {ticks.map(t => <g key={t}><line x1={PL} x2={W - PR} y1={y(t)} y2={y(t)} className="grid" /><text x={W - PR + 12} y={y(t) + 4} className="axis">{fmt(t)}</text></g>)}
      {xl.map((ms, k) => <text key={k} x={xt(ms)} y={H - 6} className="axis" textAnchor={k === 0 ? "start" : ms >= t0 + span ? "end" : "middle"}>{label(ms)}</text>)}
      <path d={`${path(s.v)}L${x(s.v.length - 1)} ${H - PB}L${x(0)} ${H - PB}Z`} fill="url(#g)" />
      {s.bench && <path d={path(s.bench)} className="bench" />}
      <path d={path(s.v)} className="line" />
    </svg>
  );
}

export function Donut({ parts, colors, center, sub }: { parts: number[]; colors: string[]; center: string; sub: string }) {
  const R = 44, C = 2 * Math.PI * R, gap = 2;
  let off = 0;
  return (
    <svg viewBox="0 0 120 120" className="donut">
      <g transform="rotate(-90 60 60)" fill="none" strokeWidth="14">
        {parts.map((p, i) => {
          const len = (p / 100) * C, el = <circle key={i} cx="60" cy="60" r={R} stroke={colors[i]} strokeDasharray={`${Math.max(0, len - gap)} ${C}`} strokeDashoffset={-off} />;
          off += len;
          return el;
        })}
      </g>
      <text x="60" y="64" textAnchor="middle" className="dnum">{center}</text>
      <text x="60" y="78" textAnchor="middle" className="dsub">{sub}</text>
    </svg>
  );
}
