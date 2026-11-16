const usd = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
const usd0 = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 });
const int = new Intl.NumberFormat("en-US");

export const money = (n: number) => usd.format(n);
export const money0 = (n: number) => usd0.format(n);
export const num = (n: number) => int.format(n);
export const sign = (n: number) => (n < 0 ? "−" : "+");
export const signed = (n: number) => sign(n) + usd.format(Math.abs(n));
export const pct = (n: number, d = 2) => (n * 100).toFixed(d) + "%";
export const spct = (n: number, d = 2) => sign(n) + Math.abs(n * 100).toFixed(d) + "%";
export const tone = (n: number) => (n < 0 ? "neg" : "pos");

export const hms = (ms: number) => new Date(ms).toISOString().slice(11, 19);
export const dmy = (s: string) => new Date(s + "T00:00:00Z").toLocaleDateString("en-GB", { day: "2-digit", month: "short", year: "numeric", timeZone: "UTC" });
export const mon = (ms: number) => new Date(ms).toLocaleDateString("en-US", { month: "short", day: "2-digit", timeZone: "UTC" });

export const ago = (s: number) => (s < 60 ? `${s}s ago` : s < 3600 ? `${Math.floor(s / 60)}m ago` : `${Math.floor(s / 3600)}h ago`);
export const agoMs = (ms: number) => ago(Math.max(0, Math.round((Date.now() - ms) / 1000)));
