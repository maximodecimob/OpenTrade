export type OrderStatus = "pending" | "partial" | "filled" | "cancelled" | "rejected";
export interface Order {
  id: string; botId: string | null; brokerId: string; symbol: string; side: "buy" | "sell"; type: "limit" | "market"; qty: number; filled: number; price: number;
  status: OrderStatus; source: string; strategy: string; broker: string; mode: string; time: number; amount: number;
}
export interface BotRow {
  id: string; name: string; class: string; description: string; brokerId: string; broker: string; clientId: string; paper: boolean; running: boolean;
  allocated: number; invested: number; utilization: number; returnPct: number | null; pnl: number; realized: number; unrealized: number;
  status: "running" | "paused" | "simulated"; online: boolean; heartbeat: number | null; keys: number;
}
export interface BrokerRow {
  id: string; name: string; paper: boolean; mask: string; equity: number; cash: number; buyingPower: number; latencyMs: number; connected: boolean;
  error: string | null; bots: number; lastSync: number | null; accountNumber?: string; accountStatus?: string;
}
export interface Series { t: number[]; v: number[]; bench?: number[]; change?: number; pct: number }
