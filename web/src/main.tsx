import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { api } from "./api";
import { Layout, Login } from "./Layout";
import { Me, UserCtx } from "./ui";
import { Positions } from "./pages/Positions";
import { Assets } from "./pages/Assets";
import { Settings } from "./pages/Settings";
import { ApiDocs } from "./pages/ApiDocs";
import { Dashboard } from "./pages/Dashboard";
import { Portfolios } from "./pages/Portfolios";
import { Bots, BotDetail } from "./pages/Bots";
import { Orders } from "./pages/Orders";
import { Brokers } from "./pages/Brokers";
import { ApiKeys } from "./pages/ApiKeys";
import "./styles.css";

function App() {
  const [user, setUser] = useState<Me | null | undefined>();
  useEffect(() => {
    api<Me>("/me").then(setUser, () => setUser(null));
    const out = () => setUser(null);
    window.addEventListener("unauth", out);
    return () => window.removeEventListener("unauth", out);
  }, []);
  if (user === undefined) return null;
  if (!user) return <Login onLogin={setUser} />;
  return (
    <UserCtx.Provider value={user}>
    <Routes>
      <Route element={<Layout user={user} onLogout={() => api("/logout", "POST").then(() => setUser(null))} />}>
        <Route index element={<Dashboard />} />
        <Route path="portfolios" element={<Portfolios />} />
        <Route path="bots" element={<Bots />} />
        <Route path="bots/:id" element={<BotDetail />} />
        <Route path="orders" element={<Orders />} />
        <Route path="brokers" element={<Brokers />} />
        <Route path="positions" element={<Positions />} />
        <Route path="assets" element={<Assets />} />
        <Route path="api-keys" element={<ApiKeys />} />
        <Route path="api-docs" element={<ApiDocs />} />
        <Route path="settings" element={<Settings onUser={setUser} />} />
        <Route path="*" element={<Navigate to="/" />} />
      </Route>
    </Routes>
    </UserCtx.Provider>
  );
}

createRoot(document.getElementById("root")!).render(<StrictMode><BrowserRouter><App /></BrowserRouter></StrictMode>);
