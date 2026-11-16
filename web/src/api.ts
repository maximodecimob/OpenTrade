import { useCallback, useEffect, useState } from "react";

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

export async function api<T = unknown>(path: string, method = "GET", body?: unknown): Promise<T> {
  const r = await fetch("/api" + path, {
    method,
    credentials: "same-origin",
    headers: { ...(method !== "GET" && { "X-OT": "1" }), ...(body !== undefined && { "Content-Type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (r.status === 401 && path !== "/login") window.dispatchEvent(new Event("unauth"));
  if (!r.ok) throw new ApiError(r.status, (await r.json().catch(() => null))?.error ?? r.statusText);
  return r.status === 204 ? (undefined as T) : r.json();
}

/** GET with optional polling. The timer pauses while the tab is hidden. */
export function useApi<T>(path: string, pollMs = 0) {
  const [data, setData] = useState<T>();
  const load = useCallback(() => api<T>(path).then(setData, () => {}), [path]);
  useEffect(() => {
    load();
    if (!pollMs) return;
    const id = setInterval(() => document.hidden || load(), pollMs);
    return () => clearInterval(id);
  }, [load, pollMs]);
  return [data, load] as const;
}
