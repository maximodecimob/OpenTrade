# Load test results: BEFORE the optimizations (app as in commit 91198d0)

- Date: 2026-11-24 11:56
- Machine: 16 logical cores, Microsoft Windows NT 10.0.26200.0, .NET 10.0.13
- CPU is % of the whole machine (100% = all cores busy). RAM is the server's peak working set. Latencies are measured by the client, in ms.

## 1. Rate limits (default configuration)

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| A. one key, one IP | 200 req, 10 parallel | 200 | 154 | 93 | 60.0 | 2.1 | 26.3 | 971.2 | 1292 | 5% / 10% | 128 MB | 10 MB | 30 | 0.5 | 4% | 200×120 429×80 |
| B. 7 keys, same IP | 900 req, 20 parallel | 900 | 477 | 318 | 66.7 | 3.5 | 14.5 | 647.9 | 1885 | 8% / 14% | 148 MB | 10 MB | 42 | 0.7 | 4% | 200×600 429×300 |
| C. 7 keys, 7 IPs | 1050 req, 20 parallel | 1050 | 370 | 296 | 80.0 | 2.2 | 12.5 | 1264.8 | 2837 | 8% / 34% | 150 MB | 11 MB | 45 | 0.9 | 1% | 200×840 429×210 |
| D. wrong logins, one IP | 12 req, 1 parallel | 12 | 69 | 0 | 0.0 | 0.3 | 39.2 | 39.2 | 39 | 0% / 0% | 0 MB | 0 MB | 0 | 0.9 | 0% | 429×7 401×5 |
| E1. window edge, before | 119 req, 10 parallel | 119 | 97 | 97 | 100.0 | 0.8 | 474.9 | 1082.3 | 1233 | 5% / 7% | 139 MB | 11 MB | 28 | 1.0 | 0% | 200×119 |
| E2. window edge, after | 150 req, 10 parallel | 150 | 6522 | 5217 | 80.0 | 1.0 | 4.2 | 4.7 | 5 | 0% / 0% | 0 MB | 0 MB | 0 | 1.0 | 13% | 200×120 429×30 |

## 2a. Fleet: many users × many bots (real rate limits, app as shipped)

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 10 users × 5 bots | 50 bots + 10 tabs (target 88 req/s) | 2650 | 76 | 76 | 100.0 | 4.5 | 500.7 | 1239.6 | 2717 | 4% / 19% | 171 MB | 19 MB | 43 | 4.1 | 0% | 200×1850 201×400 204×400 |
| 25 users × 5 bots | 125 bots + 25 tabs (target 220 req/s) | 6622 | 190 | 190 | 100.0 | 3.2 | 377.2 | 1110.6 | 2613 | 3% / 12% | 189 MB | 24 MB | 45 | 4.5 | 0% | 200×4622 201×1000 204×1000 |
| 50 users × 5 bots | 250 bots + 50 tabs (target 441 req/s) | 12804 | 368 | 368 | 100.0 | 201.6 | 1424.5 | 2558.9 | 10872 | 5% / 20% | 304 MB | 47 MB | 60 | 5.1 | 0% | 200×8932 201×1960 204×1912 |
| 100 users × 5 bots | 500 bots + 100 tabs (target 883 req/s) | 9914 | 283 | 283 | 100.0 | 1092.5 | 5048.3 | 14390.6 | 21786 | 5% / 27% | 438 MB | 179 MB | 65 | 5.7 | 0% | 200×7047 201×1458 204×1409 |

**10 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| POST /v1/orders | 400 | 89.0 | 1219.9 | 2144.4 |
| DELETE /v1/orders/{id} | 400 | 78.6 | 828.2 | 1135.1 |
| GET /api/dashboard (web) | 30 | 39.1 | 665.8 | 777.5 |
| GET /v1/bot | 450 | 4.9 | 156.1 | 1097.9 |
| GET /api/bots (web) | 60 | 33.5 | 77.5 | 77.6 |
| GET /api/orders (web) | 60 | 3.0 | 62.8 | 62.8 |
| GET /v1/bars | 450 | 2.3 | 36.1 | 40.2 |
| GET /v1/positions | 400 | 2.6 | 34.2 | 42.6 |
| GET /v1/orders?status=open | 400 | 2.6 | 5.3 | 17.0 |

**25 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| POST /v1/orders | 1000 | 104.5 | 1036.5 | 1945.6 |
| GET /api/dashboard (web) | 75 | 42.8 | 659.8 | 1094.3 |
| DELETE /v1/orders/{id} | 1000 | 53.5 | 526.1 | 1261.8 |
| GET /v1/bot | 1124 | 3.8 | 265.6 | 407.8 |
| GET /api/bots (web) | 150 | 34.3 | 85.8 | 114.9 |
| GET /api/orders (web) | 150 | 3.0 | 53.9 | 230.4 |
| GET /v1/bars | 1123 | 1.7 | 33.6 | 45.1 |
| GET /v1/orders?status=open | 1000 | 2.2 | 6.1 | 7.5 |
| GET /v1/positions | 1000 | 1.8 | 4.2 | 14.4 |

**50 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| GET /api/dashboard (web) | 150 | 876.4 | 5095.1 | 5372.3 |
| POST /v1/orders | 1960 | 730.3 | 2115.3 | 4000.4 |
| DELETE /v1/orders/{id} | 1912 | 673.6 | 2031.6 | 3790.6 |
| GET /api/bots (web) | 300 | 640.5 | 1506.6 | 1985.1 |
| GET /v1/bot | 2126 | 199.8 | 1094.1 | 1287.7 |
| GET /v1/bars | 2115 | 85.6 | 1040.3 | 1254.0 |
| GET /api/orders (web) | 300 | 137.3 | 1026.4 | 1213.7 |
| GET /v1/orders?status=open | 1967 | 43.1 | 1001.7 | 1123.4 |
| GET /v1/positions | 1974 | 57.3 | 936.7 | 1176.6 |

**100 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| GET /api/dashboard (web) | 100 | 18506.5 | 20415.7 | 21602.6 |
| GET /api/bots (web) | 157 | 6060.0 | 8839.2 | 8938.9 |
| POST /v1/orders | 1458 | 2881.2 | 6459.2 | 10131.3 |
| DELETE /v1/orders/{id} | 1409 | 2580.1 | 5414.2 | 7906.2 |
| GET /v1/bot | 1807 | 1110.7 | 3630.9 | 4553.8 |
| GET /api/orders (web) | 157 | 1475.8 | 3276.2 | 4829.1 |
| GET /v1/positions | 1630 | 289.6 | 2698.4 | 4271.8 |
| GET /v1/orders?status=open | 1507 | 376.8 | 2329.8 | 3364.2 |
| GET /v1/bars | 1689 | 406.1 | 2329.6 | 4509.0 |

## 2b. Fleet with SQLite WAL mode (experiment)

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 10 users × 5 bots | 50 bots + 10 tabs (target 88 req/s) | 2646 | 76 | 76 | 100.0 | 5.1 | 670.8 | 1449.4 | 3003 | 4% / 17% | 178 MB | 22 MB | 51 | 4.1 | 0% | 200×1846 201×400 204×400 |
| 25 users × 5 bots | 125 bots + 25 tabs (target 220 req/s) | 6609 | 190 | 190 | 100.0 | 6.0 | 573.8 | 1600.3 | 3320 | 3% / 16% | 198 MB | 21 MB | 51 | 4.4 | 0% | 200×4611 201×1000 204×998 |
| 50 users × 5 bots | 250 bots + 50 tabs (target 441 req/s) | 12354 | 371 | 371 | 100.0 | 185.3 | 1831.1 | 3138.2 | 9196 | 6% / 27% | 303 MB | 38 MB | 63 | 5.1 | 0% | 200×8566 201×1941 204×1847 |
| 100 users × 5 bots | 500 bots + 100 tabs (target 883 req/s) | 10720 | 283 | 283 | 100.0 | 1304.5 | 3538.1 | 12717.4 | 24043 | 4% / 25% | 488 MB | 216 MB | 70 | 5.7 | 0% | 200×7653 201×1612 204×1455 |

**10 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| POST /v1/orders | 400 | 190.0 | 1374.8 | 2223.3 |
| DELETE /v1/orders/{id} | 400 | 195.2 | 1126.2 | 1594.2 |
| GET /api/dashboard (web) | 30 | 35.0 | 686.1 | 807.5 |
| GET /v1/bot | 448 | 30.7 | 220.2 | 1115.8 |
| GET /api/bots (web) | 60 | 42.4 | 80.6 | 440.4 |
| GET /api/orders (web) | 60 | 2.7 | 57.8 | 146.7 |
| GET /v1/bars | 448 | 2.2 | 37.2 | 47.1 |
| GET /v1/positions | 400 | 2.2 | 32.5 | 45.3 |
| GET /v1/orders?status=open | 400 | 2.0 | 6.4 | 15.9 |

**25 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| POST /v1/orders | 1000 | 208.6 | 1461.1 | 2131.3 |
| DELETE /v1/orders/{id} | 998 | 158.5 | 930.4 | 2105.4 |
| GET /api/dashboard (web) | 75 | 53.9 | 895.9 | 1112.0 |
| GET /v1/bot | 1119 | 35.6 | 423.1 | 606.9 |
| GET /api/orders (web) | 150 | 2.9 | 213.1 | 521.6 |
| GET /api/bots (web) | 150 | 42.3 | 161.8 | 552.4 |
| GET /v1/bars | 1117 | 1.6 | 47.5 | 283.5 |
| GET /v1/positions | 1000 | 1.6 | 6.3 | 35.0 |
| GET /v1/orders?status=open | 1000 | 1.9 | 4.0 | 6.2 |

**50 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| GET /api/dashboard (web) | 150 | 810.5 | 4793.2 | 6291.4 |
| DELETE /v1/orders/{id} | 1847 | 773.6 | 2935.3 | 4459.8 |
| POST /v1/orders | 1941 | 516.3 | 2150.7 | 3957.5 |
| GET /v1/bot | 1976 | 195.7 | 1735.7 | 2322.8 |
| GET /api/bots (web) | 286 | 362.6 | 1422.0 | 2090.2 |
| GET /api/orders (web) | 286 | 118.7 | 1034.7 | 1258.5 |
| GET /v1/positions | 1955 | 59.9 | 1023.5 | 1202.7 |
| GET /v1/bars | 1967 | 93.8 | 889.0 | 1152.9 |
| GET /v1/orders?status=open | 1946 | 68.3 | 743.6 | 920.7 |

**100 users × 5 bots**, per endpoint:

| Endpoint | Requests | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| GET /api/dashboard (web) | 100 | 21209.4 | 23846.3 | 23913.3 |
| GET /api/bots (web) | 185 | 4306.3 | 5182.7 | 6013.9 |
| POST /v1/orders | 1612 | 2393.7 | 4483.4 | 8237.1 |
| DELETE /v1/orders/{id} | 1455 | 1786.1 | 3802.5 | 7596.5 |
| GET /v1/bot | 1870 | 1281.4 | 2822.6 | 6622.9 |
| GET /v1/positions | 1786 | 919.3 | 2467.5 | 3009.7 |
| GET /v1/bars | 1822 | 958.4 | 2456.0 | 3429.1 |
| GET /v1/orders?status=open | 1705 | 960.1 | 1985.1 | 3261.6 |
| GET /api/orders (web) | 185 | 1005.9 | 1822.5 | 1920.4 |

## 3. Raw ceiling (rate limits off, broker latency 0)

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| GET /v1/bot (key auth + DB) | 1 parallel | 6990 | 874 | 874 | 100.0 | 1.1 | 1.9 | 2.7 | 49 | 10% / 29% | 144 MB | 9 MB | 51 | 2.0 | 1% | 200×6990 |
| GET /v1/bot (key auth + DB) | 8 parallel | 65836 | 8228 | 8228 | 100.0 | 0.6 | 2.8 | 4.2 | 766 | 36% / 54% | 145 MB | 13 MB | 51 | 2.1 | 6% | 200×65836 |
| GET /v1/bot (key auth + DB) | 32 parallel | 61236 | 7652 | 7652 | 100.0 | 2.3 | 10.7 | 19.6 | 2206 | 33% / 48% | 152 MB | 17 MB | 51 | 2.3 | 4% | 200×61236 |
| GET /v1/bot (key auth + DB) | 128 parallel | 45659 | 5702 | 5702 | 100.0 | 14.1 | 28.9 | 52.7 | 3640 | 32% / 62% | 152 MB | 19 MB | 53 | 3.2 | 3% | 200×45659 |
| GET /v1/bot (key auth + DB) | 512 parallel | 65882 | 8208 | 8208 | 100.0 | 58.3 | 90.4 | 153.2 | 1930 | 33% / 46% | 161 MB | 25 MB | 53 | 4.0 | 4% | 200×65882 |
| GET /v1/bars (cached market data) | 1 parallel | 12002 | 1500 | 1500 | 100.0 | 0.6 | 0.9 | 2.2 | 16 | 7% / 18% | 159 MB | 19 MB | 49 | 4.0 | 1% | 200×12002 |
| GET /v1/bars (cached market data) | 8 parallel | 59217 | 7400 | 7400 | 100.0 | 0.7 | 2.9 | 3.7 | 944 | 33% / 44% | 153 MB | 23 MB | 43 | 4.0 | 5% | 200×59217 |
| GET /v1/bars (cached market data) | 32 parallel | 65054 | 8115 | 8115 | 100.0 | 3.7 | 6.6 | 18.0 | 2195 | 38% / 54% | 157 MB | 25 MB | 38 | 4.0 | 5% | 200×65054 |
| GET /v1/bars (cached market data) | 128 parallel | 60623 | 7551 | 7551 | 100.0 | 14.5 | 28.3 | 43.1 | 2049 | 35% / 54% | 157 MB | 24 MB | 35 | 4.0 | 6% | 200×60623 |
| GET /v1/bars (cached market data) | 512 parallel | 53273 | 6639 | 6639 | 100.0 | 67.1 | 108.5 | 237.8 | 2083 | 32% / 56% | 161 MB | 32 MB | 35 | 4.0 | 4% | 200×53273 |
| GET /api/dashboard (cookie) | 1 parallel | 6019 | 752 | 752 | 100.0 | 1.2 | 2.5 | 3.7 | 49 | 7% / 20% | 172 MB | 28 MB | 34 | 4.0 | 1% | 200×6019 |
| GET /api/dashboard (cookie) | 8 parallel | 27964 | 3494 | 3494 | 100.0 | 1.6 | 4.3 | 6.1 | 22 | 34% / 47% | 169 MB | 32 MB | 34 | 4.0 | 3% | 200×27964 |
| GET /api/dashboard (cookie) | 32 parallel | 32370 | 4045 | 4045 | 100.0 | 7.8 | 18.6 | 24.9 | 44 | 42% / 60% | 179 MB | 29 MB | 34 | 4.0 | 2% | 200×32370 |
| GET /api/dashboard (cookie) | 128 parallel | 33133 | 4132 | 4132 | 100.0 | 28.1 | 48.6 | 60.1 | 99 | 46% / 67% | 175 MB | 32 MB | 34 | 4.0 | 2% | 200×33133 |
| GET /api/dashboard (cookie) | 512 parallel | 26816 | 3282 | 3282 | 100.0 | 152.9 | 212.6 | 236.1 | 254 | 40% / 68% | 183 MB | 33 MB | 35 | 4.0 | 2% | 200×26816 |
| GET /api/orders page (cookie) | 1 parallel | 6344 | 793 | 793 | 100.0 | 1.2 | 1.8 | 3.7 | 66 | 8% / 20% | 168 MB | 20 MB | 35 | 4.0 | 1% | 200×6344 |
| GET /api/orders page (cookie) | 8 parallel | 27514 | 3439 | 3439 | 100.0 | 1.6 | 5.3 | 6.5 | 19 | 35% / 42% | 158 MB | 25 MB | 33 | 4.0 | 3% | 200×27514 |
| GET /api/orders page (cookie) | 32 parallel | 27608 | 3446 | 3446 | 100.0 | 9.4 | 19.8 | 27.1 | 50 | 39% / 51% | 165 MB | 28 MB | 33 | 4.0 | 3% | 200×27608 |
| GET /api/orders page (cookie) | 128 parallel | 30641 | 3822 | 3822 | 100.0 | 31.9 | 52.9 | 70.3 | 117 | 35% / 49% | 168 MB | 30 MB | 32 | 4.0 | 2% | 200×30641 |
| GET /api/orders page (cookie) | 512 parallel | 37806 | 4700 | 4700 | 100.0 | 108.4 | 131.4 | 144.2 | 188 | 40% / 52% | 172 MB | 31 MB | 32 | 4.0 | 2% | 200×37806 |
| POST /api/login (PBKDF2) | 1 parallel | 291 | 36 | 36 | 100.0 | 27.2 | 30.6 | 32.5 | 37 | 7% / 10% | 172 MB | 28 MB | 32 | 4.0 | 0% | 200×291 |
| POST /api/login (PBKDF2) | 4 parallel | 1062 | 132 | 132 | 100.0 | 30.0 | 35.6 | 37.6 | 42 | 25% / 27% | 165 MB | 35 MB | 32 | 4.0 | 0% | 200×1062 |
| POST /api/login (PBKDF2) | 16 parallel | 3135 | 390 | 390 | 100.0 | 39.6 | 51.8 | 65.4 | 107 | 84% / 95% | 165 MB | 35 MB | 33 | 4.0 | 0% | 200×3135 |
| POST /api/login (PBKDF2) | 64 parallel | 3438 | 423 | 423 | 100.0 | 141.9 | 224.2 | 249.6 | 281 | 92% / 97% | 165 MB | 35 MB | 34 | 4.0 | 0% | 200×3438 |

## 4a. Orders, broker latency 0 ms (app as shipped)

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| one bot, market buy | 1 parallel | 1055 | 132 | 132 | 100.0 | 7.4 | 11.5 | 13.6 | 25 | 9% / 12% | 151 MB | 19 MB | 28 | 4.5 | 0% | 201×1055 |
| one bot, market buy | 4 parallel | 1644 | 205 | 205 | 100.0 | 18.5 | 31.9 | 36.0 | 802 | 9% / 13% | 167 MB | 29 MB | 28 | 4.9 | 0% | 201×1644 |
| one bot, market buy | 16 parallel | 1613 | 198 | 198 | 100.0 | 78.2 | 155.3 | 181.7 | 502 | 8% / 10% | 159 MB | 17 MB | 28 | 5.4 | 0% | 201×1613 |
| one bot, market buy | 64 parallel | 1575 | 185 | 185 | 100.0 | 318.1 | 571.3 | 774.9 | 926 | 7% / 9% | 171 MB | 22 MB | 33 | 5.9 | 0% | 201×1575 |
| N bots, market buy | 1 parallel | 1574 | 197 | 197 | 100.0 | 5.0 | 8.4 | 9.8 | 13 | 6% / 8% | 157 MB | 16 MB | 33 | 6.3 | 0% | 201×1574 |
| N bots, market buy | 4 parallel | 2397 | 290 | 290 | 100.0 | 3.7 | 9.4 | 319.3 | 802 | 7% / 11% | 169 MB | 20 MB | 33 | 7.0 | 0% | 201×2397 |
| N bots, market buy | 16 parallel | 2913 | 337 | 337 | 100.0 | 5.5 | 317.4 | 801.5 | 3316 | 9% / 14% | 174 MB | 22 MB | 37 | 8.3 | 0% | 201×2913 |
| N bots, market buy | 64 parallel | 2062 | 232 | 232 | 100.0 | 190.8 | 594.2 | 1319.1 | 4768 | 10% / 16% | 183 MB | 26 MB | 37 | 10.6 | 0% | 201×2062 |
| N bots, market buy | 256 parallel | 1788 | 185 | 185 | 100.0 | 1149.6 | 2230.0 | 3079.6 | 6828 | 11% / 21% | 199 MB | 27 MB | 40 | 11.7 | 0% | 201×1788 |
| one bot's history grows | orders 0-1000 | 1000 | 111 | 111 | 100.0 | 8.6 | 12.6 | 16.2 | 28 | 6% / 7% | 172 MB | 22 MB | 40 | 12.0 | 0% | 201×1000 |
| one bot's history grows | orders 1000-2000 | 1000 | 89 | 89 | 100.0 | 10.9 | 14.3 | 17.4 | 177 | 6% / 7% | 187 MB | 25 MB | 40 | 12.3 | 0% | 201×1000 |
| one bot's history grows | orders 2000-3000 | 1000 | 65 | 65 | 100.0 | 14.9 | 20.0 | 24.2 | 35 | 6% / 9% | 202 MB | 27 MB | 22 | 12.6 | 0% | 201×1000 |
| one bot's history grows | orders 3000-4000 | 1000 | 38 | 38 | 100.0 | 25.4 | 35.7 | 44.3 | 68 | 6% / 11% | 220 MB | 33 MB | 22 | 12.9 | 0% | 201×1000 |
| one bot's history grows | orders 4000-5000 | 1000 | 30 | 30 | 100.0 | 31.9 | 44.6 | 52.8 | 85 | 6% / 11% | 217 MB | 41 MB | 33 | 13.1 | 0% | 201×1000 |
| one bot's history grows | orders 5000-6000 | 1000 | 29 | 29 | 100.0 | 31.8 | 48.8 | 65.7 | 193 | 6% / 8% | 264 MB | 47 MB | 21 | 13.4 | 0% | 201×1000 |
| web UI order (no bot) | 1 parallel | 2615 | 322 | 322 | 100.0 | 2.6 | 4.7 | 7.7 | 170 | 6% / 9% | 214 MB | 39 MB | 21 | 14.0 | 1% | 200×2615 |
| web UI order (no bot) | 16 parallel | 2778 | 326 | 326 | 100.0 | 4.7 | 167.2 | 954.1 | 5952 | 7% / 10% | 220 MB | 41 MB | 33 | 14.6 | 0% | 200×2778 |
| web UI order (no bot) | 64 parallel | 2453 | 267 | 267 | 100.0 | 131.8 | 530.0 | 1549.1 | 3309 | 6% / 12% | 183 MB | 34 MB | 37 | 15.1 | 0% | 200×2453 |
| cancel open order (64 bots) | 64 parallel | 2684 | 254 | 254 | 100.0 | 13.9 | 977.2 | 4466.3 | 10268 | 4% / 13% | 199 MB | 39 MB | 73 | 19.0 | 0% | 204×2684 |

## 4b. Orders, broker latency 40 ms (like a real Alpaca round trip)

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| one bot, market buy | 1 parallel | 82 | 10 | 10 | 100.0 | 95.5 | 110.1 | 122.6 | 123 | 4% / 8% | 145 MB | 9 MB | 27 | 2.9 | 0% | 201×82 |
| one bot, market buy | 16 parallel | 96 | 10 | 10 | 100.0 | 1524.6 | 1551.0 | 2411.9 | 2412 | 3% / 8% | 151 MB | 11 MB | 31 | 4.1 | 0% | 201×96 |
| N bots, market buy | 16 parallel | 444 | 51 | 51 | 100.0 | 97.9 | 1036.3 | 3445.8 | 5809 | 1% / 5% | 151 MB | 16 MB | 36 | 4.2 | 0% | 201×444 |
| N bots, market buy | 64 parallel | 647 | 60 | 60 | 100.0 | 137.4 | 3755.2 | 5809.9 | 8629 | 3% / 13% | 184 MB | 27 MB | 53 | 4.4 | 0% | 201×647 |
| N bots, market buy | 256 parallel | 1229 | 122 | 122 | 100.0 | 1685.5 | 3429.1 | 5167.3 | 6816 | 3% / 10% | 224 MB | 31 MB | 53 | 4.9 | 0% | 201×1229 |

## 4c. Orders with SQLite WAL mode (experiment), broker latency 0 ms

| Step | Load | Requests | req/s | OK req/s | OK % | p50 | p95 | p99 | max | Server CPU avg / peak | Server RAM peak | GC heap peak | Threads | DB MB | Client CPU | Status codes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| one bot, market buy | 1 parallel | 768 | 96 | 96 | 100.0 | 10.3 | 17.8 | 19.8 | 23 | 9% / 13% | 146 MB | 11 MB | 28 | 4.3 | 0% | 201×768 |
| one bot, market buy | 16 parallel | 1283 | 158 | 158 | 100.0 | 103.2 | 138.8 | 492.0 | 858 | 11% / 15% | 161 MB | 27 MB | 33 | 4.7 | 0% | 201×1283 |
| N bots, market buy | 16 parallel | 2799 | 327 | 327 | 100.0 | 4.6 | 169.1 | 943.7 | 6862 | 7% / 12% | 159 MB | 17 MB | 37 | 6.0 | 0% | 201×2799 |
| N bots, market buy | 64 parallel | 1762 | 198 | 198 | 100.0 | 140.1 | 974.0 | 2576.6 | 4752 | 6% / 11% | 169 MB | 17 MB | 44 | 6.5 | 0% | 201×1762 |
| N bots, market buy | 256 parallel | 2207 | 204 | 204 | 100.0 | 927.5 | 2088.8 | 3278.0 | 5837 | 5% / 11% | 207 MB | 23 MB | 50 | 7.2 | 0% | 201×2207 |
| one bot's history grows | orders 0-1000 | 1000 | 167 | 167 | 100.0 | 5.4 | 10.4 | 13.8 | 26 | 6% / 7% | 196 MB | 21 MB | 50 | 7.5 | 0% | 201×1000 |
| one bot's history grows | orders 1000-2000 | 1000 | 77 | 77 | 100.0 | 12.5 | 18.9 | 25.5 | 74 | 6% / 8% | 178 MB | 30 MB | 50 | 7.7 | 0% | 201×1000 |
| one bot's history grows | orders 2000-3000 | 1000 | 52 | 52 | 100.0 | 18.5 | 24.8 | 29.0 | 75 | 6% / 9% | 202 MB | 26 MB | 23 | 8.0 | 0% | 201×1000 |
| one bot's history grows | orders 3000-4000 | 1000 | 47 | 47 | 100.0 | 20.7 | 27.6 | 33.2 | 60 | 6% / 10% | 192 MB | 26 MB | 22 | 8.3 | 0% | 201×1000 |
| one bot's history grows | orders 4000-5000 | 1000 | 35 | 35 | 100.0 | 26.7 | 43.1 | 56.6 | 184 | 6% / 9% | 218 MB | 31 MB | 37 | 8.6 | 0% | 201×1000 |
| one bot's history grows | orders 5000-6000 | 1000 | 26 | 26 | 100.0 | 36.5 | 51.8 | 61.2 | 100 | 6% / 9% | 278 MB | 47 MB | 35 | 8.9 | 0% | 201×1000 |

## Notes

- [1. Rate limits (default configuration)] server: broker latency 0 ms, /v1 limit 120/min per key, global limit 600/min per IP, login 5/min, max 50 API keys, SQLite journal default (rollback journal, as the app ships)
- A: one API key got 120 of 200 requests through (expected 120 = RateLimit:V1PerMinute), the rest were 429.
- B: 7 keys behind ONE IP got 600 of 900 through (expected 600 = RateLimit:GlobalPerMinute per IP, even though 7×120 = 840).
- C: 7 keys on 7 different IPs got 840 of 1050 through (expected 840 = 7 × 120).
- D: 12 wrong passwords from one IP: 429×7 401×5 (expected 5×401 then 429).
- E: fixed-window edge: one key got 240 requests through in about 2.6 s around the window change (the 'limit' of 120/min is per window, not per any 60 s).
- [2a. Fleet: many users × many bots (real rate limits, app as shipped)] server: broker latency 30 ms, /v1 limit 120/min per key, global limit 600/min per IP, login 5/min, max 100000 API keys, SQLite journal default (rollback journal, as the app ships). Each bot: 100 req/min in a loop of 6 calls (bot, bars, positions, open orders, place limit order, cancel it). Each user: 1 browser tab (dashboard every 10 s, orders + bots every 5 s) = ~30 req/min.
- Step 10 users × 5 bots = 50 bots is the largest fleet the DEFAULT configuration allows (Keys:MaxCount = 50 API keys in total).
- [2a. Fleet: many users × many bots (real rate limits, app as shipped)] stopped the ramp at 100 users: p95 5048 ms, OK 100.0%.
- [2b. Fleet with SQLite WAL mode (experiment)] server: broker latency 30 ms, /v1 limit 120/min per key, global limit 600/min per IP, login 5/min, max 100000 API keys, SQLite journal WAL (experiment). Each bot: 100 req/min in a loop of 6 calls (bot, bars, positions, open orders, place limit order, cancel it). Each user: 1 browser tab (dashboard every 10 s, orders + bots every 5 s) = ~30 req/min.
- [2b. Fleet with SQLite WAL mode (experiment)] stopped the ramp at 100 users: p95 3538 ms, OK 100.0%.
- [3. Raw ceiling (rate limits off, broker latency 0)] server: broker latency 0 ms, /v1 limit 100000000/min per key, global limit 100000000/min per IP, login 100000000/min, max 100000 API keys, SQLite journal default (rollback journal, as the app ships)
- [3. Raw ceiling (rate limits off, broker latency 0)] the load generator runs on the same machine and uses CPU too (column 'Client CPU'), so the server's real ceiling on a dedicated machine is higher.
- [4a. Orders, broker latency 0 ms (app as shipped)] server: broker latency 0 ms, /v1 limit 100000000/min per key, global limit 100000000/min per IP, login 100000000/min, max 100000 API keys, SQLite journal default (rollback journal, as the app ships)
- [4b. Orders, broker latency 40 ms (like a real Alpaca round trip)] server: broker latency 40 ms, /v1 limit 100000000/min per key, global limit 100000000/min per IP, login 100000000/min, max 100000 API keys, SQLite journal default (rollback journal, as the app ships)
- [4c. Orders with SQLite WAL mode (experiment), broker latency 0 ms] server: broker latency 0 ms, /v1 limit 100000000/min per key, global limit 100000000/min per IP, login 100000000/min, max 100000 API keys, SQLite journal WAL (experiment)
