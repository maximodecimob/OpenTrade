# OpenTrade maximum traffic test

- Date: 2026-11-24 12:15
- Machine: 16 logical cores, Microsoft Windows NT 10.0.26200.0, .NET 10.0.13
- CPU is % of the whole machine (100% = all cores busy). RAM is the server's peak working set. Latencies are measured by the client, in ms.

## MAX: how much traffic OpenTrade handles

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 10 users × 10 bots | 100 bots + 10 tabs (target 171 req/s) | 1770 | 176 | 176 | 100.0 | 4.3 | 81.3 | 178.3 | 238 | 8% / 39% | 166 MB | 19 MB | 51 | 4.1 | 2% | 200×1268 201×281 204×221 |
| 50 users × 10 bots | 500 bots + 50 tabs (target 858 req/s) | 8575 | 852 | 852 | 100.0 | 5.8 | 82.1 | 122.1 | 162 | 20% / 39% | 236 MB | 22 MB | 51 | 4.8 | 2% | 200×6250 201×1323 204×1002 |
| 100 users × 10 bots | 1000 bots + 100 tabs (target 1716 req/s) | 17332 | 1703 | 1703 | 100.0 | 9.8 | 154.6 | 218.7 | 302 | 25% / 52% | 301 MB | 40 MB | 53 | 6.4 | 3% | 200×12558 201×2707 204×2067 |
| 150 users × 10 bots | 1500 bots + 150 tabs (target 2575 req/s) | 25685 | 2535 | 2535 | 100.0 | 28.0 | 952.3 | 1096.1 | 1201 | 31% / 70% | 458 MB | 133 MB | 53 | 10.4 | 3% | 200×18746 201×3936 204×3003 |
| 200 users × 10 bots | 2000 bots + 200 tabs (target 3433 req/s) | 28562 | 2737 | 2737 | 100.0 | 159.6 | 1769.6 | 2002.7 | 2251 | 33% / 56% | 596 MB | 336 MB | 53 | 17.7 | 2% | 200×19989 201×4573 204×4000 |
| BURST (no pauses) | 256 parallel | 53141 | 5300 | 5300 | 100.0 | 47.2 | 66.4 | 107.4 | 178 | 59% / 74% | 521 MB | 154 MB | 44 | 17.8 | 2% | 200×53141 |

**10 users × 10 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| POST /v1/orders | 281 | 69.4 | 194.6 | 230.4 |
| GET /api/dashboard (web) | 16 | 7.8 | 156.3 | 156.3 |
| DELETE /v1/orders/{id} | 221 | 40.7 | 60.3 | 75.6 |
| GET /api/orders (web) | 26 | 4.7 | 53.6 | 57.9 |
| GET /v1/bot | 300 | 3.5 | 47.6 | 92.8 |
| GET /v1/bars | 300 | 2.9 | 45.8 | 79.3 |
| GET /api/bots (web) | 26 | 2.8 | 34.1 | 97.0 |
| GET /v1/positions | 300 | 3.3 | 19.6 | 49.9 |
| GET /v1/orders?status=open | 300 | 3.2 | 8.0 | 15.4 |

**50 users × 10 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| POST /v1/orders | 1323 | 70.2 | 130.3 | 142.1 |
| GET /api/dashboard (web) | 50 | 3.5 | 99.1 | 117.6 |
| DELETE /v1/orders/{id} | 1002 | 41.9 | 80.0 | 87.2 |
| GET /v1/bars | 1500 | 3.4 | 45.2 | 50.8 |
| GET /v1/bot | 1500 | 3.6 | 36.6 | 43.0 |
| GET /v1/positions | 1500 | 3.7 | 35.0 | 42.6 |
| GET /api/bots (web) | 100 | 5.9 | 22.3 | 54.5 |
| GET /api/orders (web) | 100 | 7.4 | 14.5 | 21.1 |
| GET /v1/orders?status=open | 1500 | 4.4 | 13.0 | 16.4 |

**100 users × 10 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| DELETE /v1/orders/{id} | 2067 | 86.1 | 228.0 | 248.0 |
| POST /v1/orders | 2707 | 113.5 | 188.5 | 228.8 |
| GET /api/dashboard (web) | 120 | 5.8 | 145.8 | 146.3 |
| GET /v1/bot | 3000 | 5.4 | 64.3 | 87.3 |
| GET /v1/bars | 3000 | 4.2 | 38.2 | 46.8 |
| GET /api/orders (web) | 219 | 9.4 | 36.1 | 51.0 |
| GET /v1/positions | 3000 | 5.7 | 35.5 | 48.6 |
| GET /api/bots (web) | 219 | 3.5 | 31.7 | 106.4 |
| GET /v1/orders?status=open | 3000 | 8.6 | 26.0 | 32.4 |

**150 users × 10 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| DELETE /v1/orders/{id} | 3003 | 818.2 | 1101.5 | 1111.1 |
| POST /v1/orders | 3936 | 380.8 | 858.5 | 962.0 |
| GET /api/dashboard (web) | 150 | 8.9 | 127.4 | 176.5 |
| GET /api/orders (web) | 298 | 16.4 | 126.9 | 160.7 |
| GET /v1/bot | 4500 | 35.0 | 99.8 | 156.7 |
| GET /v1/positions | 4500 | 8.0 | 92.9 | 145.1 |
| GET /v1/bars | 4500 | 10.5 | 87.6 | 131.6 |
| GET /api/bots (web) | 298 | 7.0 | 71.6 | 120.0 |
| GET /v1/orders?status=open | 4500 | 10.3 | 29.8 | 35.7 |

**200 users × 10 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| POST /v1/orders | 4573 | 1448.9 | 2018.4 | 2155.1 |
| DELETE /v1/orders/{id} | 4000 | 1610.1 | 1878.2 | 1990.1 |
| GET /api/bots (web) | 398 | 85.6 | 1434.0 | 1973.2 |
| GET /api/dashboard (web) | 200 | 82.3 | 614.6 | 789.6 |
| GET /v1/positions | 4696 | 41.9 | 458.9 | 584.0 |
| GET /v1/bot | 4910 | 75.2 | 419.9 | 570.6 |
| GET /api/orders (web) | 398 | 96.7 | 407.6 | 488.7 |
| GET /v1/bars | 4742 | 35.0 | 332.4 | 447.2 |
| GET /v1/orders?status=open | 4645 | 146.0 | 303.3 | 425.1 |

## Notes

- [MAX: how much traffic OpenTrade handles] server: broker latency 30 ms, /v1 limit 100000000/min per key, global limit 100000000/min per IP, login 5/min, max 1000000 API keys
