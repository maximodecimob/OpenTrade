import { FormEvent, useState } from "react";
import { api, useApi } from "../api";
import { Badge, Confirm, Empty, Field, Head, Me, Modal, Panel, PasswordInput, Table, useMe } from "../ui";

interface U { id: string; name: string; email: string; role: Me["role"]; active: boolean; created: number }
const ROLES: [Me["role"], string][] = [["admin", "Administrator"], ["trader", "Trader"], ["viewer", "Viewer (read only)"]];
const TONE = { admin: "green", trader: "blue", viewer: "grey" } as const;

function UserForm({ user, onClose, onDone }: { user?: U; onClose: () => void; onDone: () => void }) {
  const [err, setErr] = useState(""), [busy, setBusy] = useState(false), [pw, setPw] = useState("");
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setBusy(true); setErr("");
    try {
      if (user) await api(`/users/${user.id}`, "PUT", { name: f.get("name"), role: f.get("role"), active: f.get("active") === "on" });
      else await api("/users", "POST", { name: f.get("name"), email: f.get("email"), role: f.get("role"), password: pw });
      onDone();
    } catch (x) { setErr((x as Error).message); setBusy(false); }
  }
  return (
    <Modal title={user ? "Edit user" : "Add user"} onClose={onClose}>
      <form onSubmit={submit} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
        <Field label="Name"><input className="inp" name="name" defaultValue={user?.name} required maxLength={60} autoFocus /></Field>
        {!user && <Field label="Email"><input className="inp" name="email" type="email" required maxLength={254} /></Field>}
        <Field label="Role"><select className="inp" name="role" defaultValue={user?.role ?? "trader"}>{ROLES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
        {!user && <Field label="Initial password" hint="At least 12 characters. Ask the user to change it after signing in."><PasswordInput value={pw} onChange={setPw} autoComplete="new-password" required /></Field>}
        {user && <label className="chk"><label><input type="checkbox" name="active" defaultChecked={user.active} />Account is active (can sign in)</label></label>}
        {err && <div className="err" role="alert">{err}</div>}
        <div className="actions"><button type="button" className="btn" onClick={onClose}>Cancel</button><button className="btn pri" disabled={busy}>{user ? "Save" : "Add user"}</button></div>
      </form>
    </Modal>
  );
}

function ResetPassword({ user, onClose }: { user: U; onClose: () => void }) {
  const [pw, setPw] = useState(""), [err, setErr] = useState("");
  async function submit(e: FormEvent) {
    e.preventDefault();
    try { await api(`/users/${user.id}/password`, "POST", { password: pw }); onClose(); } catch (x) { setErr((x as Error).message); }
  }
  return (
    <Modal title={`Reset password · ${user.name}`} onClose={onClose}>
      <form onSubmit={submit} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
        <Field label="New password" hint="At least 12 characters."><PasswordInput value={pw} onChange={setPw} autoComplete="new-password" required autoFocus /></Field>
        {err && <div className="err" role="alert">{err}</div>}
        <div className="actions"><button type="button" className="btn" onClick={onClose}>Cancel</button><button className="btn pri">Set password</button></div>
      </form>
    </Modal>
  );
}

function Users({ me }: { me: Me }) {
  const [users, reload] = useApi<U[]>("/users", 15000);
  const [edit, setEdit] = useState<U | "new" | null>(null), [reset, setReset] = useState<U | null>(null);
  if (!users) return null;
  return (
    <Panel title="Users" note="Everyone who can sign in to this workspace." action={<button className="btn pri" onClick={() => setEdit("new")}>+ Add user</button>}>
      {users.length === 0 ? <Empty>No users.</Empty> : (
        <Table cols="1.6fr 2fr 1.2fr 1fr 2fr" head={["Name", "Email", "Role", "Status", "Actions"]}>
          {users.map(u => (
            <div className="tr tall" key={u.id}>
              <span className="name">{u.name}{u.id === me.id && <small>You</small>}</span><span className="muted">{u.email}</span>
              <span><Badge tone={TONE[u.role]}>{u.role.toUpperCase()}</Badge></span>
              <span><Badge tone={u.active ? "green" : "grey"}>{u.active ? "Active" : "Disabled"}</Badge></span>
              <span className="actions" style={{ justifyContent: "flex-start", gap: 16 }}>
                <button className="link" onClick={() => setEdit(u)}>Edit</button>
                <button className="link mu" onClick={() => setReset(u)}>Reset password</button>
                {u.id !== me.id && <Confirm onYes={async () => { if (window.confirm(`Delete ${u.name}?`)) { await api(`/users/${u.id}`, "DELETE"); reload(); } }}><span className="neg">Delete</span></Confirm>}
              </span>
            </div>
          ))}
        </Table>
      )}
      {edit && <UserForm user={edit === "new" ? undefined : edit} onClose={() => setEdit(null)} onDone={() => { setEdit(null); reload(); }} />}
      {reset && <ResetPassword user={reset} onClose={() => setReset(null)} />}
    </Panel>
  );
}

export function Settings({ onUser }: { onUser: (u: Me) => void }) {
  const me = useMe();
  const [msg, setMsg] = useState(""), [err, setErr] = useState(""), [pmsg, setPmsg] = useState(""), [perr, setPerr] = useState("");
  const [cur, setCur] = useState(""), [pw, setPw] = useState(""), [pw2, setPw2] = useState("");
  async function saveProfile(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setMsg(""); setErr("");
    try { onUser(await api<Me>("/me", "PUT", { name: new FormData(e.currentTarget).get("name") })); setMsg("Profile saved"); } catch (x) { setErr((x as Error).message); }
  }
  async function savePassword(e: FormEvent) {
    e.preventDefault();
    setPmsg(""); setPerr("");
    if (pw !== pw2) return setPerr("The new passwords do not match");
    try { await api("/me/password", "POST", { current: cur, password: pw }); setPmsg("Password changed"); setCur(""); setPw(""); setPw2(""); } catch (x) { setPerr((x as Error).message); }
  }
  return (
    <>
      <Head title="Settings" sub="Your profile, password and workspace users." />
      <div className="cols" style={{ gridTemplateColumns: "1fr 1fr", alignItems: "start" }}>
        <Panel title="Profile">
          <form onSubmit={saveProfile} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
            <Field label="Name"><input className="inp" name="name" defaultValue={me.name} required maxLength={60} /></Field>
            <Field label="Email"><input className="inp" value={me.email} disabled /></Field>
            <Field label="Role"><input className="inp" value={me.role} disabled style={{ textTransform: "capitalize" }} /></Field>
            {err && <div className="err" role="alert">{err}</div>}{msg && <div className="pos sm">{msg}</div>}
            <div><button className="btn pri">Save profile</button></div>
          </form>
        </Panel>
        <Panel title="Change password">
          <form onSubmit={savePassword} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
            <input type="text" autoComplete="username" value={me.email} readOnly hidden />
            <Field label="Current password"><PasswordInput value={cur} onChange={setCur} autoComplete="current-password" required /></Field>
            <Field label="New password" hint="At least 12 characters."><PasswordInput value={pw} onChange={setPw} autoComplete="new-password" required /></Field>
            <Field label="Repeat new password"><PasswordInput value={pw2} onChange={setPw2} autoComplete="new-password" required /></Field>
            {perr && <div className="err" role="alert">{perr}</div>}{pmsg && <div className="pos sm">{pmsg}</div>}
            <div><button className="btn pri">Change password</button></div>
          </form>
        </Panel>
      </div>
      {me.isAdmin && <Users me={me} />}
    </>
  );
}
