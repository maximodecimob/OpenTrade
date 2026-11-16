import { FormEvent, useState } from "react";
import { NavLink, Outlet, useLocation, useNavigate } from "react-router-dom";
import { api } from "./api";
import { Icon, Me, PasswordInput, useMe } from "./ui";

const NAV = [
  ["/", "dashboard", "Dashboard"], ["/portfolios", "portfolio", "Portfolios"], ["/bots", "bot", "Strategies & Bots"], ["/orders", "orders", "Orders"],
  ["/positions", "positions", "Positions"], ["/assets", "assets", "Assets"], ["/brokers", "brokers", "Brokers"], ["/api-keys", "api", "API Management"], ["/settings", "settings", "Settings"],
] as const;
const TITLES: Record<string, string> = {
  portfolios: "Portfolios", bots: "Strategies & Bots", orders: "Orders", positions: "Positions", assets: "Assets", brokers: "Brokers",
  "api-keys": "API Management", "api-docs": "API documentation", settings: "Settings",
};

const Logo = () => <div className="logo"><b>↗</b><span>OpenTrade<small>MANAGER</small></span></div>;

export function Layout({ user, onLogout }: { user: Me; onLogout: () => void }) {
  const { isAdmin } = useMe();
  const nav = useNavigate(), { pathname } = useLocation();
  const [seg] = pathname.split("/").filter(Boolean);
  const [q, setQ] = useState("");
  return (
    <div className="shell">
      <aside className="side">
        <Logo />
        <div className="ws"><div className="cap">Workspace</div><div>OpenTrade</div></div>
        <div className="cap" style={{ padding: "0 6px" }}>Management</div>
        <nav className="nav">
          {NAV.filter(([to]) => isAdmin || to !== "/api-keys").map(([to, ic, label]) => <NavLink key={to} to={to} end={to === "/"}><Icon n={ic} />{label}</NavLink>)}
        </nav>
        <div className="grow" />
        <NavLink className="help" to="/api-docs"><Icon n="help" />API documentation</NavLink>
      </aside>
      <div className="main">
        <header className="top">
          <div className="crumb">
            <span>Workspace</span>
            <b>{seg ? TITLES[seg] ?? seg : "Dashboard"}</b>
          </div>
          <form className="search" onSubmit={e => { e.preventDefault(); nav(`/orders?q=${encodeURIComponent(q.trim())}`); }}>
            <Icon n="search" /><input value={q} onChange={e => setQ(e.target.value)} placeholder="Search anything…" maxLength={40} aria-label="Search orders" />
            <kbd>⌘ K</kbd>
          </form>
          <Icon n="bell" />
          <div className="user"><button onClick={onLogout} title="Sign out"><span>{user.name}<small>{{ admin: "Administrator", trader: "Trader", viewer: "Viewer" }[user.role]}</small></span><Icon n="chevron" /></button></div>
        </header>
        <div className="page">
          <Outlet />
          <div className="foot"><span>All amounts in USD</span><span>{new Date().toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })}</span></div>
        </div>
      </div>
    </div>
  );
}

export function Login({ onLogin }: { onLogin: (u: Me) => void }) {
  const [err, setErr] = useState(""), [busy, setBusy] = useState(false);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setBusy(true); setErr("");
    try { onLogin(await api<Me>("/login", "POST", { email: f.get("email"), password: f.get("password") })); }
    catch (x) { setErr((x as Error).message); setBusy(false); }
  }
  return (
    <div className="center">
      <form className="card login" onSubmit={submit}>
        <Logo />
        <label>Email<input className="inp" name="email" type="email" autoComplete="username" required autoFocus maxLength={254} /></label>
        <label>Password<PasswordInput name="password" autoComplete="current-password" required /></label>
        {err && <div className="err" role="alert">{err}</div>}
        <button className="btn pri" disabled={busy} style={{ justifyContent: "center" }}>Sign in</button>
      </form>
    </div>
  );
}
