# 4. Specific, interesting and hard things

These are the parts of the project that were specific to trading, that surprised me, or that took me the longest. The numbers come
from the load tests ([03-load-testing.md](03-load-testing.md)).

---

## Specific

### A bot cannot spend more than its share
The bots run outside the app, so we cannot be sure that they know their own positions or how much money they have left. For this reason each bot gets a percentage of the account, and the USD value is calculated from the current equity. Before a buy is sent to the broker, `CheckBotLimits` checks that `positions + open buys + new order ≤ % × equity`. A market order has no price yet, so we use the
last 1-minute price. A bot also cannot sell more than it has, so it cannot go short by mistake. This is solved.

### Two orders at the same time
If a bot sends two orders at the same moment, both checks see the same free money and both pass, so the bot can spend twice its
share. I fixed this with a `SemaphoreSlim` per bot around "check + send + save". Other bots do not wait, but the orders of one bot now go one by one: about 150 orders/s, or about 10/s with a real broker of 40 ms. This is solved.

### The sync and a new order insert the same order
Every 15 s `OrderSync` copies the orders from the broker into the database. If a bot places an order during a sync, the sync can insert it first, and then the bot's save fails on the unique index of `BrokerOrderId`. Now the code catches the `DbUpdateException`, loads the row that the sync saved and adds the bot to it. You only see this bug with concurrent tests, but in production it would break orders.
This is solved.

### Profit per bot
Alpaca only knows the positions of the whole account, not of each bot. To show the profit of a bot, `Ledger.Compute` replays the bot's own fills in order with the average cost method. This is solved.

---

## Interesting

### Many bots on one computer share one IP limit
The app has two limits: 120 requests/min per API key and 600 requests/min per IP. Bots on the same computer have the same IP, so 7 bots do not get 7 × 120 = 840 requests, they get 600 together. 
The test measured exactly this: 7 keys from one IP got 600 OK of 900, and the same 7 keys from 7 IPs got 840. So one computer can only run 5 bots at full speed. There is also a limit of 50 API keys in total, so at most 50 bots can trade on the whole server.

This is only partly solved. Before, these limits were fixed in the code. Now they are settings (`RateLimit:GlobalPerMinute`, `RateLimit:V1PerMinute` and `Keys:MaxCount`), but the default values are the same, so with the defaults the problem is still there.

### Caching the task, not the result
If 50 requests ask for the account balance at the same time and the cache is empty, a normal cache sends 50 calls to Alpaca `BrokerHub` saves the running `Task` in the cache instead of the result, so the 50 requests wait for the same call. 
If the call fails, the task is removed at once, so errors are not cached. This was already in the app, and now I use the same idea for the dashboard and bots pages.

### Login is the most expensive request
Passwords use PBKDF2 with 210,000 iterations, so a stolen hash is slow to crack. The cost is that one login uses about 27 ms of CPU.
With the limits off, 64 logins in parallel gave about 420 logins/s and used 92 % of the CPU, while simple reads gave about 8,000/s with 36 % CPU. So the limit of 5 logins/min per IP protects the passwords and also the CPU. This is not a problem, it works as designed.

### Measuring the server and not the test
If the test and the server run in the same process, you do not know whose CPU and RAM you are measuring. That is why the test starts the app in a separate process, and the app prints its CPU, RAM, threads and database size every 250 ms. 
Another problem was that all the simulated users come from my computer, so they would share one IP limit. To avoid this, each simulated user sends a different IP in the `X-Forwarded-For` header, which the app accepts from localhost (where a reverse proxy would be).

---

## Hard

### Low CPU but slow requests: SQLite was sleeping
With only 50 bots the CPU was at 5 %, but some requests took more than 1 s, and only the requests that write were slow. 
The reason is that SQLite allows one writer at a time. When two writes collide, Microsoft.Data.Sqlite waits with `Thread.Sleep(150)` and tries again, so each collision costs 150 ms, and the sleeping threads block the thread pool, so other requests also wait.

I changed three things: WAL mode, so reads do not wait for writes; one async queue (`SemaphoreSlim`) before every `SaveChanges`, so writes never collide and the sleep never happens; and `synchronous=NORMAL`, so a commit does not wait for the disk. 
With 64 bots placing orders, the speed went from 232 to 1,370 orders/s and the p95 from 594 ms to 72 ms. This is solved, but there is still only one writer, so the maximum is about 700 order operations/s under full load.

### Missing indexes
The `Orders` table had no index on `BotId` and `BrokerId`, and every bot request filters by them, so with many orders each request read the whole table. 
I added the two indexes. `EnsureCreated` does not change an existing database, so they are created at startup with `CREATE INDEX IF NOT EXISTS`. This is solved.

### A bot gets slower the more orders it has
The capital check replays all the fills of the bot on every order. From 1,000 to 6,000 orders, one order went from 8.6 ms to 32 ms.
Now it loads only the 9 columns it needs, which is faster, but it still grows with the number of orders.
But i have no idea how to solve complety the problem.

### The dashboard took 30 seconds
With 400 users the dashboard took 30 s, for two reasons. First, it loaded the history of every broker account one by one (`await` inside a `foreach`), so 400 accounts × 30 ms = 12 s. 
Now it loads them all at the same time with `Task.WhenAll`. Second, every user sees the same dashboard, but every open tab built it again every 5–10 s. Now the page is shared for 2 s, and any change in the web UI deletes it at once. 
A test found one bug here: a new bot showed "offline" for 2 s, so now the first use of a key also deletes the shared page.
This is solved.

### Reads that write
`GET /v1/bot` also saved the "last used" time of the key. With 2,000 bots that is 200 writes/s, so a simple read had to wait for writes (p50 360 ms). 
Now the app counts in memory and saves all keys in one transaction every 10 s (`KeyUsage`). Only the first request of a key is saved at once. The p50 went from 360 ms to 80 ms. This is solved.

### No migrations
The database is created with `EnsureCreated`, which cannot change an existing database, so when I changed the allocations from USD to percentages, the old databases broke. 
I added a small `SchemaUpgrade` at startup that adds or removes columns. It works for now, but in
a new project I would use EF Core migrations from the start. 
Because EF Core migrations are much more flexible, but I only found out about them online after developing the app.

### Testing without the broker
The unit tests and the rate-limit tests do not use the Alpaca broker that I connected, because Alpaca has its own rate limit (about 200 requests per minute). Instead, they use a simulated broker: the unit tests use FakeBroker and the load tests use SimBroker. If I used the real broker to measure the maximum capacity of the app, Alpaca would block the requests long before OpenTrade reached its own limit.
