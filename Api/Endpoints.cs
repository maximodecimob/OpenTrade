using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace OpenTrade.Api;

public sealed record LoginReq(string? Email, string? Password);
public sealed record ProfileReq(string? Name);
public sealed record PasswordReq(string? Current, string? Password);
public sealed record UserReq(string? Email, string? Name, string? Role, string? Password, bool? Active);
public sealed record BrokerReq(string? Name, string? KeyId, string? Secret, bool Paper);
public sealed record BotReq(string? Name, string? Class, string? Description, string? BrokerId, decimal? AllocationPct);
public sealed record AllocationItem(string? Id, decimal? Pct);
public sealed record AllocationsReq(AllocationItem[]? Items);
public sealed record RegisterReq(string? Email, string? Name, string? Password);
public sealed record KeyReq(string? Name, string? BrokerId, string? BotId, string[]? Perms, int? ExpiresInDays);
public sealed record PlaceReq(string? BrokerId, string? BotId, string? Symbol, string? Side, string? Type, decimal? Qty, decimal? LimitPrice, string? TimeInForce);
public sealed record CloseReq(string? BrokerId, string? Symbol);

public static partial class Endpoints
{
    public static readonly string[] AllowedPerms = ["read", "positions", "trade"];
    static readonly Regex EmailRx = EmailRegex();
    static readonly Regex KeyIdRx = KeyIdRegex();
    static readonly Regex SymbolRx = SymbolRegex();

    [GeneratedRegex("^[A-Z0-9./-]{1,15}$")] private static partial Regex SymbolRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailRegex();
    [GeneratedRegex(@"^[A-Za-z0-9]{8,64}$")] private static partial Regex KeyIdRegex();

    public static IResult Err(int code, string msg) => Results.Json(new { error = msg }, statusCode: code);

    public static ClaimsPrincipal Principal(User u) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, u.Id), new Claim(ClaimTypes.Name, u.Name), new Claim(ClaimTypes.Email, u.Email), new Claim(ClaimTypes.Role, u.Role.ToString()),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    static string Uid(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    static object MeDto(User u) => new { u.Id, u.Name, u.Email, Role = u.Role.ToString().ToLowerInvariant() };
    static object UserDto(User u) => new { u.Id, u.Name, u.Email, Role = u.Role.ToString().ToLowerInvariant(), u.Active, created = u.CreatedMs };

    static string Mask(string keyId) => keyId.Length <= 8 ? "••••" : keyId[..4] + "••••" + keyId[^4..];

    // ---------- order helpers ----------
    sealed record Lookups(Dictionary<string, Broker> Brokers, Dictionary<string, Bot> Bots);

    static async Task<Lookups> Load(OtDb db) => new(
        await db.Brokers.AsNoTracking().ToDictionaryAsync(b => b.Id),
        await db.Bots.AsNoTracking().ToDictionaryAsync(b => b.Id));

    static object OrderDto(Order o, Lookups l)
    {
        l.Brokers.TryGetValue(o.BrokerId, out var b);
        var bot = o.BotId is null ? null : l.Bots.GetValueOrDefault(o.BotId);
        return new
        {
            o.Id, o.BotId, o.BrokerId, o.Symbol, o.Side, o.Type, o.Qty, o.Filled, price = o.Price, o.Status, o.Source,
            strategy = bot?.Name ?? (o.Source == "external" ? "External" : "Manual"),
            broker = b?.Name ?? "—", mode = $"Alpaca · {(b?.Paper == false ? "Live" : "Paper")}",
            time = o.CreatedMs, amount = o.Filled * o.Price * (o.Side == Side.Buy ? -1 : 1),
        };
    }

    static IQueryable<Order> FilterOrders(IQueryable<Order> q, string? status, string? text)
    {
        if (status == "open") q = q.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Partial);
        else if (Enum.TryParse<OrderStatus>(status, true, out var st)) q = q.Where(o => o.Status == st);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Trim().Replace("%", "").Replace("_", "");
            if (t.Length is > 0 and <= 40) q = q.Where(o => EF.Functions.Like(o.Symbol, "%" + t + "%") || EF.Functions.Like(o.Id, "%" + t + "%"));
        }
        return q;
    }

    static string Csv(object? v)
    {
        var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    static (string Period, string Tf)? RangeOf(string? r) => r switch
    {
        "1D" => ("1D", "15Min"), "1W" => ("1W", "1H"), "1M" => ("1M", "1D"), "3M" => ("3M", "1D"),
        "YTD" => ($"{Math.Max(1, DateTime.UtcNow.DayOfYear)}D", "1D"), "ALL" => ("1A", "1D"), _ => null,
    };

    /// <summary>
    /// Drops the leading points where the account had no money yet. The first deposit (for example the 100,000 USD a new paper account starts with)
    /// is not a gain, so profit is measured from the first funded point onwards.
    /// </summary>
    static History FromFunding(History h)
    {
        var i = Array.FindIndex(h.V, v => v > 0.01);
        h = i <= 0 ? h : new History(h.T[i..], h.V[i..]);
        return StripFlows(h);
    }

    /// <summary>
    /// Removes deposits and withdrawals from the curve. Trading cannot move an account by more than 20% between two consecutive points,
    /// so a bigger jump is money moved in or out (or a broker artefact) and is subtracted from everything after it.
    /// The curve then shows performance only: it starts at the funded value and changes with trading results.
    /// </summary>
    static History StripFlows(History h)
    {
        if (h.V.Length < 2) return h;
        var v = new double[h.V.Length];
        v[0] = h.V[0];
        double offset = 0;
        for (var i = 1; i < v.Length; i++)
        {
            var prev = h.V[i - 1];
            var step = h.V[i] - prev;
            if (prev > 0.01 && Math.Abs(step) > 0.2 * prev) offset += step;
            v[i] = h.V[i] - offset;
        }
        return new History(h.T, v);
    }

    /// <summary>Equity curve summed over the given brokers; only timestamps present in every account are kept.</summary>
    static async Task<(long[] T, double[] V)> Curve(BrokerHub hub, IEnumerable<Broker> brokers, string period, string tf)
    {
        var hs = new List<History>();
        foreach (var b in brokers)
        {
            try { hs.Add(FromFunding(await hub.History(b, period, tf))); }
            catch (BrokerException) { /* skip broken account */ }
            catch (CryptographicException) { /* skip broken account */ }
        }
        hs = hs.Where(h => h.T.Length > 0).ToList();
        if (hs.Count == 0) return ([], []);
        var sum = new SortedDictionary<long, (double V, int N)>();
        foreach (var h in hs)
            for (var i = 0; i < h.T.Length; i++)
            {
                var (v, n) = sum.GetValueOrDefault(h.T[i]);
                sum[h.T[i]] = (v + h.V[i], n + 1);
            }
        var rows = sum.Where(kv => kv.Value.N == hs.Count).ToArray();
        return (rows.Select(r => r.Key).ToArray(), rows.Select(r => r.Value.V).ToArray());
    }

    static async Task<decimal> Change(BrokerHub hub, IEnumerable<Broker> brokers, string period) =>
        await Curve(hub, brokers, period, "1D") is { V.Length: >= 2 } c ? (decimal)(c.V[^1] - c.V[0]) : 0m;

    static string ClassName(string c) => c switch { "us_equity" => "US equities", "crypto" => "Crypto", "us_option" => "Options", _ => "Other" };

    // ======================================================================
    public static void MapAppApi(this WebApplication app)
    {
        var pub = app.MapGroup("/api");
        var api = app.MapGroup("/api").RequireAuthorization();
        var trade = app.MapGroup("/api").RequireAuthorization(p => p.RequireAuthenticatedUser().RequireRole("Admin", "Trader"));
        var admin = app.MapGroup("/api").RequireAuthorization(p => p.RequireAuthenticatedUser().RequireRole("Admin"));

        // ---------- session ----------
        pub.MapPost("/login", async (LoginReq r, OtDb db, HttpContext c) =>
        {
            if (r.Email is not { Length: > 0 and <= 254 } || r.Password is not { Length: > 0 and <= 128 }) return Err(401, "Invalid email or password");
            var email = r.Email.Trim().ToLowerInvariant();
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (u is null || !u.Active) { Passwords.Burn(r.Password); return Err(401, "Invalid email or password"); }
            if (!Passwords.Verify(r.Password, u.Salt, u.Hash)) return Err(401, "Invalid email or password");
            await c.SignInAsync(Principal(u));
            return Results.Ok(MeDto(u));
        }).RequireRateLimiting("login");
        pub.MapPost("/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.NoContent(); });

        // ---------- self-registration ----------
        // The very first account becomes the admin. Later accounts get Registration:Role (viewer by default, never admin) and can be changed by an admin.
        // Turn it off with Registration:Enabled=false.
        var registerLock = new SemaphoreSlim(1, 1);
        pub.MapGet("/setup", async (OtDb db, IConfiguration cfg) => Results.Ok(new
        {
            firstRun = !await db.Users.AnyAsync(),
            registration = cfg.GetValue("Registration:Enabled", true) || !await db.Users.AnyAsync(),
        }));
        pub.MapPost("/register", async (RegisterReq r, OtDb db, HttpContext c, IConfiguration cfg) =>
        {
            await registerLock.WaitAsync();
            try
            {
                var first = !await db.Users.AnyAsync();
                if (!first && !cfg.GetValue("Registration:Enabled", true)) return Err(403, "Registration is disabled. Ask an administrator to create your account.");
                var email = r.Email?.Trim().ToLowerInvariant() ?? "";
                if (email.Length > 254 || !EmailRx.IsMatch(email)) return Err(400, "Invalid email address");
                if (r.Name?.Trim() is not { Length: > 0 and <= 60 } name) return Err(400, "Name is required (max 60 characters)");
                if (Passwords.Validate(r.Password) is { } bad) return Err(400, bad);
                if (await db.Users.AnyAsync(x => x.Email == email)) return Err(409, "A user with this email already exists");
                var role = first ? Role.Admin
                    : Enum.TryParse<Role>(cfg["Registration:Role"], true, out var cfgRole) && cfgRole != Role.Admin ? cfgRole : Role.Viewer;
                var u = new User { Email = email, Name = name, Role = role };
                (u.Salt, u.Hash) = Passwords.Create(r.Password!);
                db.Users.Add(u);
                await db.SaveChangesAsync();
                await c.SignInAsync(Principal(u));
                return Results.Json(MeDto(u), statusCode: 201);
            }
            finally { registerLock.Release(); }
        }).RequireRateLimiting("login");
        pub.MapGet("/me", async (OtDb db, HttpContext c) =>
        {
            if (c.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Uid(c.User));
            return u is null ? Results.Unauthorized() : Results.Ok(MeDto(u));
        });
        api.MapPut("/me", async (ProfileReq r, OtDb db, HttpContext c) =>
        {
            if (r.Name?.Trim() is not { Length: > 0 and <= 60 } name) return Err(400, "Name is required (max 60 characters)");
            var u = await db.Users.FirstAsync(x => x.Id == Uid(c.User));
            u.Name = name;
            await db.SaveChangesAsync();
            await c.SignInAsync(Principal(u));
            return Results.Ok(MeDto(u));
        });
        api.MapPost("/me/password", async (PasswordReq r, OtDb db, HttpContext c) =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == Uid(c.User));
            if (string.IsNullOrEmpty(r.Current) || !Passwords.Verify(r.Current, u.Salt, u.Hash)) return Err(400, "Current password is incorrect");
            if (Passwords.Validate(r.Password) is { } bad) return Err(400, bad);
            (u.Salt, u.Hash) = Passwords.Create(r.Password!);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- users (admin) ----------
        admin.MapGet("/users", async (OtDb db) => Results.Ok((await db.Users.AsNoTracking().OrderBy(u => u.CreatedMs).ToListAsync()).Select(UserDto)));
        admin.MapPost("/users", async (UserReq r, OtDb db) =>
        {
            var email = r.Email?.Trim().ToLowerInvariant() ?? "";
            if (email.Length > 254 || !EmailRx.IsMatch(email)) return Err(400, "Invalid email address");
            if (r.Name?.Trim() is not { Length: > 0 and <= 60 } name) return Err(400, "Name is required (max 60 characters)");
            if (!Enum.TryParse<Role>(r.Role, true, out var role)) return Err(400, "Role must be admin, trader or viewer");
            if (Passwords.Validate(r.Password) is { } bad) return Err(400, bad);
            if (await db.Users.AnyAsync(x => x.Email == email)) return Err(409, "A user with this email already exists");
            var u = new User { Email = email, Name = name, Role = role };
            (u.Salt, u.Hash) = Passwords.Create(r.Password!);
            db.Users.Add(u);
            await db.SaveChangesAsync();
            return Results.Ok(UserDto(u));
        });
        admin.MapPut("/users/{id}", async (string id, UserReq r, OtDb db, HttpContext c) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Err(404, "User not found");
            var role = u.Role;
            if (r.Role is not null && !Enum.TryParse(r.Role, true, out role)) return Err(400, "Role must be admin, trader or viewer");
            var active = r.Active ?? u.Active;
            if (r.Name is not null && r.Name.Trim() is not { Length: > 0 and <= 60 }) return Err(400, "Name is required (max 60 characters)");
            var losesAdmin = u.Role == Role.Admin && u.Active && (role != Role.Admin || !active);
            if (losesAdmin && !await db.Users.AnyAsync(x => x.Id != id && x.Role == Role.Admin && x.Active)) return Err(409, "There must be at least one active admin");
            if (u.Id == Uid(c.User) && !active) return Err(409, "You cannot deactivate your own account");
            if (r.Name is not null) u.Name = r.Name.Trim();
            u.Role = role; u.Active = active;
            await db.SaveChangesAsync();
            return Results.Ok(UserDto(u));
        });
        admin.MapPost("/users/{id}/password", async (string id, PasswordReq r, OtDb db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Err(404, "User not found");
            if (Passwords.Validate(r.Password) is { } bad) return Err(400, bad);
            (u.Salt, u.Hash) = Passwords.Create(r.Password!);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        admin.MapDelete("/users/{id}", async (string id, OtDb db, HttpContext c) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Err(404, "User not found");
            if (u.Id == Uid(c.User)) return Err(409, "You cannot delete your own account");
            if (u.Role == Role.Admin && u.Active && !await db.Users.AnyAsync(x => x.Id != id && x.Role == Role.Admin && x.Active)) return Err(409, "There must be at least one active admin");
            db.Users.Remove(u);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- dashboard / portfolios ----------
        api.MapGet("/dashboard", async (OtDb db, BrokerHub hub) =>
        {
            var brokers = await db.Brokers.AsNoTracking().ToListAsync();
            var snaps = await hub.Snapshots(brokers);
            var ok = snaps.Where(s => s.Account is not null).ToList();
            var live = ok.Select(s => s.Broker).ToList();
            var nav = ok.Sum(s => s.Account!.Equity);
            var cash = ok.Sum(s => s.Account!.Cash);
            var day = ok.Sum(s => s.Account!.Equity - s.Account.LastEquity);
            var (month, total) = (await Change(hub, live, "1M"), await Change(hub, live, "1A"));
            var positions = ok.SelectMany(s => s.Positions).ToList();
            var invested = positions.Sum(p => Math.Abs(p.MarketValue));
            var denom = invested + Math.Max(0, cash);
            var classes = positions.GroupBy(p => ClassName(p.AssetClass)).Select(g => new { Name = g.Key, Pct = denom == 0 ? 0 : (double)(g.Sum(p => Math.Abs(p.MarketValue)) / denom * 100) }).ToList();
            if (denom > 0) classes.Add(new { Name = "Cash", Pct = (double)(Math.Max(0, cash) / denom * 100) });
            var bots = await db.Bots.AsNoTracking().ToListAsync();
            Alloc.Apply(bots, Alloc.Equity(snaps));
            var l = await Load(db);
            var recent = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Filled).OrderByDescending(o => o.CreatedMs).Take(5).ToListAsync();
            decimal Pct(decimal x, decimal basis) => basis == 0 ? 0 : x / basis;
            return Results.Ok(new
            {
                brokers = brokers.Count, connected = ok.Count, errors = snaps.Where(s => s.Error is not null).Select(s => new { broker = s.Broker.Name, error = s.Error }),
                anyLive = brokers.Any(b => !b.Paper),
                nav, day, dayPct = Pct(day, nav - day), month, monthPct = Pct(month, nav - month), total, totalPct = Pct(total, nav - total), cash, cashPct = Pct(cash, nav),
                activeBots = bots.Count(b => b.Running), totalBots = bots.Count,
                openOrders = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Partial),
                positions = positions.Count, assetClasses = classes,
                bots = bots.Where(b => b.AllocPct > 0).Select(b => new { b.Id, b.Name, b.Class, b.Allocated, allocationPct = b.AllocPct }),
                orders = recent.Select(o => OrderDto(o, l)),
            });
        });

        api.MapGet("/series/{key}", async (string key, string? range, OtDb db, BrokerHub hub) =>
        {
            if (RangeOf(range) is not var (period, tf)) return Err(404, "Unknown range");
            var brokers = key == "portfolio" ? await db.Brokers.AsNoTracking().ToListAsync() : await db.Brokers.AsNoTracking().Where(b => b.Id == key).ToListAsync();
            if (brokers.Count == 0) return key == "portfolio" ? Results.Ok(new { t = Array.Empty<long>(), v = Array.Empty<double>(), pct = 0d, change = 0d }) : Err(404, "Not found");
            var (t, v) = await Curve(hub, brokers, period, tf);
            var change = v.Length >= 2 ? v[^1] - v[0] : 0;
            return Results.Ok(new { t, v, change, pct = v.Length >= 2 && v[0] != 0 ? change / v[0] : 0 });
        });

        api.MapGet("/portfolios", async (OtDb db, BrokerHub hub) =>
        {
            var brokers = await db.Brokers.AsNoTracking().OrderBy(b => b.CreatedMs).ToListAsync();
            var snaps = await hub.Snapshots(brokers);
            var bots = await db.Bots.AsNoTracking().ToListAsync();
            return Results.Ok(new
            {
                portfolios = snaps.Select(s => new
                {
                    s.Broker.Id, s.Broker.Name, s.Broker.Paper, s.Error,
                    sub = $"Alpaca · {(s.Broker.Paper ? "Paper" : "Live")} · {bots.Count(b => b.BrokerId == s.Broker.Id)} bots",
                    nav = s.Account?.Equity ?? 0, cash = s.Account?.Cash ?? 0, deployed = s.Positions.Sum(p => Math.Abs(p.MarketValue)),
                    dailyPnl = s.Account is null ? 0 : s.Account.Equity - s.Account.LastEquity, positions = s.Positions.Count,
                    bots = bots.Where(b => b.BrokerId == s.Broker.Id).Select(b => b.Name),
                }),
                nav = snaps.Sum(s => s.Account?.Equity ?? 0),
            });
        });

        // ---------- positions ----------
        api.MapGet("/positions", async (OtDb db, BrokerHub hub) =>
        {
            var brokers = await db.Brokers.AsNoTracking().OrderBy(b => b.CreatedMs).ToListAsync();
            var snaps = await hub.Snapshots(brokers);
            var rows = snaps.SelectMany(s => s.Positions.Select(p => new
            {
                brokerId = s.Broker.Id, broker = s.Broker.Name, s.Broker.Paper, p.Symbol, p.Qty, avg = p.AvgEntry, last = p.Last, value = p.MarketValue,
                pnl = p.UnrealizedPl, pnlPct = p.UnrealizedPlpc, cls = ClassName(p.AssetClass),
            })).OrderByDescending(p => Math.Abs(p.value)).ToList();
            return Results.Ok(new
            {
                items = rows, errors = snaps.Where(s => s.Error is not null).Select(s => new { broker = s.Broker.Name, error = s.Error }),
                totals = new { value = rows.Sum(r => r.value), pnl = rows.Sum(r => r.pnl), count = rows.Count, winners = rows.Count(r => r.pnl > 0), losers = rows.Count(r => r.pnl < 0) },
            });
        });
        trade.MapPost("/positions/close", async (CloseReq r, OtDb db, BrokerHub hub, EventLog log) =>
        {
            var b = r.BrokerId is null ? null : await db.Brokers.FirstOrDefaultAsync(x => x.Id == r.BrokerId);
            if (b is null) return Err(404, "Broker not found");
            if (string.IsNullOrWhiteSpace(r.Symbol) || r.Symbol.Length > 15) return Err(400, "Invalid symbol");
            await hub.Gateway(b).ClosePosition(r.Symbol.Trim().ToUpperInvariant());
            hub.Invalidate(b.Id);
            log.Add(b.Name, "Position closed: " + r.Symbol.Trim().ToUpperInvariant());
            return Results.NoContent();
        });

        // ---------- assets ----------
        api.MapGet("/assets", async (OtDb db, BrokerHub hub, string? q, string? cls, int page = 1, int size = 20) =>
        {
            var b = await db.Brokers.AsNoTracking().OrderBy(x => x.CreatedMs).FirstOrDefaultAsync();
            if (b is null) return Err(409, "Connect a broker first to browse tradable assets");
            size = Math.Clamp(size, 5, 100);
            IEnumerable<AssetInfo> all = (await hub.Assets(b)).Where(a => a.Tradable);
            if (!string.IsNullOrEmpty(cls)) all = all.Where(a => a.Class == cls);
            if (!string.IsNullOrWhiteSpace(q) && q.Length <= 40)
            {
                var t = q.Trim();
                all = all.Where(a => a.Symbol.StartsWith(t, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
            }
            var list = all.OrderBy(a => a.Symbol, StringComparer.Ordinal).ToList();
            var pages = Math.Max(1, (list.Count + size - 1) / size);
            page = Math.Clamp(page, 1, pages);
            return Results.Ok(new { total = list.Count, page, pages, size, brokerId = b.Id, items = list.Skip((page - 1) * size).Take(size) });
        });

        // ---------- quote (latest price, used to estimate what an order costs) ----------
        api.MapGet("/quote", async (OtDb db, BrokerHub hub, string? symbol, string? brokerId) =>
        {
            var sym = symbol?.Trim().ToUpperInvariant() ?? "";
            if (!SymbolRx.IsMatch(sym)) return Err(400, "Invalid symbol");
            var b = await db.Brokers.AsNoTracking().OrderBy(x => x.CreatedMs).FirstOrDefaultAsync(x => brokerId == null || x.Id == brokerId);
            if (b is null) return Err(409, "Connect a broker first");
            var bars = await hub.Bars(b, sym, "1Min", 1);
            if (bars.Count == 0) bars = await hub.Bars(b, sym, "1Day", 1);
            if (bars.Count == 0) return Err(404, "No price available for " + sym);
            return Results.Ok(new { symbol = sym, price = bars[^1].Close, time = bars[^1].T });
        });

        // ---------- orders ----------
        api.MapGet("/orders", async (OtDb db, string? status, string? q, string? brokerId, string? botId, int page = 1, int size = 10) =>
        {
            size = Math.Clamp(size, 1, 100);
            IQueryable<Order> scope = db.Orders.AsNoTracking();
            if (!string.IsNullOrEmpty(brokerId)) scope = scope.Where(o => o.BrokerId == brokerId);
            if (!string.IsNullOrEmpty(botId)) scope = scope.Where(o => o.BotId == botId);
            var byStatus = (await scope.GroupBy(o => o.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N);
            int N(OrderStatus s) => byStatus.GetValueOrDefault(s);
            var f = FilterOrders(scope, status, q);
            var total = await f.CountAsync();
            var pages = Math.Max(1, (total + size - 1) / size);
            page = Math.Clamp(page, 1, pages);
            var items = await f.OrderByDescending(o => o.CreatedMs).Skip((page - 1) * size).Take(size).ToListAsync();
            var l = await Load(db);
            return Results.Ok(new
            {
                total, page, pages, size,
                counts = new
                {
                    all = byStatus.Values.Sum(), open = N(OrderStatus.Pending) + N(OrderStatus.Partial), filled = N(OrderStatus.Filled), cancelled = N(OrderStatus.Cancelled),
                    rejected = N(OrderStatus.Rejected), pending = N(OrderStatus.Pending), partial = N(OrderStatus.Partial),
                },
                items = items.Select(o => OrderDto(o, l)),
            });
        });

        api.MapGet("/orders/export.csv", async (OtDb db, string? status, string? q) =>
        {
            var rows = await FilterOrders(db.Orders.AsNoTracking(), status, q).OrderByDescending(o => o.CreatedMs).Take(5000).ToListAsync();
            var l = await Load(db);
            var sb = new StringBuilder("id,time_utc,symbol,side,type,qty,filled,price,status,strategy,broker,source\n");
            foreach (var o in rows)
            {
                var strategy = o.BotId is not null && l.Bots.TryGetValue(o.BotId, out var bt) ? bt.Name : o.Source == "external" ? "External" : "Manual";
                string[] cells =
                [
                    o.Id, DateTimeOffset.FromUnixTimeMilliseconds(o.CreatedMs).ToString("u", CultureInfo.InvariantCulture), o.Symbol, o.Side.ToString().ToLowerInvariant(),
                    o.Type.ToString().ToLowerInvariant(), o.Qty.ToString(CultureInfo.InvariantCulture), o.Filled.ToString(CultureInfo.InvariantCulture),
                    o.Price.ToString(CultureInfo.InvariantCulture), o.Status.ToString().ToLowerInvariant(),
                    strategy, l.Brokers.GetValueOrDefault(o.BrokerId)?.Name ?? "", o.Source,
                ];
                sb.AppendJoin(',', cells.Select(c => Csv(c))).Append('\n');
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "orders.csv");
        });

        trade.MapPost("/orders", async (PlaceReq r, OtDb db, OrderService svc) =>
        {
            var broker = r.BrokerId is null ? null : await db.Brokers.FirstOrDefaultAsync(b => b.Id == r.BrokerId);
            if (broker is null) return Err(400, "Choose a broker account");
            Bot? bot = null;
            if (!string.IsNullOrEmpty(r.BotId))
            {
                bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == r.BotId);
                if (bot is null || bot.BrokerId != broker.Id) return Err(400, "That bot does not trade on this broker account");
                if (!bot.Running) return Err(423, "Bot is paused");
            }
            var id = OrderService.NewId();
            var (n, error) = OrderService.Validate(new OrderRequest(r.Symbol, r.Side, r.Type, r.Qty, r.LimitPrice, r.TimeInForce), id);
            if (n is null) return Err(400, error!);
            var o = await svc.Place(broker, bot, n, "ui");
            return Results.Ok(OrderDto(o, await Load(db)));
        });
        trade.MapPost("/orders/{id}/cancel", async (string id, OtDb db, OrderService svc) =>
        {
            var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == id);
            if (o is null) return Err(404, "Order not found");
            if (!o.Open) return Err(409, "Order is not open");
            return await svc.Cancel(o) is { } msg ? Err(409, msg) : Results.NoContent();
        });
        trade.MapPost("/orders/cancel-open", async (OtDb db, OrderService svc, string? brokerId) =>
        {
            var before = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Partial);
            var brokers = await db.Brokers.Where(b => brokerId == null || b.Id == brokerId).ToListAsync();
            foreach (var b in brokers) await svc.CancelAll(b);
            var after = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Partial);
            return Results.Ok(new { cancelled = Math.Max(0, before - after) });
        });

        // ---------- brokers ----------
        object BrokerDto(Broker b, AccountInfo? a, string? error, int bots) => new
        {
            b.Id, b.Name, b.Provider, b.Paper, mask = Mask(b.KeyId), equity = a?.Equity ?? 0, cash = a?.Cash ?? 0, buyingPower = a?.BuyingPower ?? 0,
            latencyMs = b.LatencyMs, connected = error is null && a is not null, error = error ?? b.LastError, bots, lastSync = b.LastSyncMs, accountNumber = a?.Number, accountStatus = a?.Status,
        };

        api.MapGet("/brokers", async (OtDb db, BrokerHub hub) =>
        {
            var brokers = await db.Brokers.AsNoTracking().OrderBy(b => b.CreatedMs).ToListAsync();
            var snaps = await hub.Snapshots(brokers);
            var bots = await db.Bots.AsNoTracking().ToListAsync();
            return Results.Ok(snaps.Select(s => BrokerDto(s.Broker, s.Account, s.Error, bots.Count(x => x.BrokerId == s.Broker.Id))));
        });
        api.MapGet("/brokers/events", (EventLog log) => Results.Ok(log.Latest(10)));

        admin.MapPost("/brokers", async (BrokerReq r, OtDb db, BrokerHub hub, IConfiguration cfg, EventLog log, OrderSync sync) =>
        {
            if (r.Name?.Trim() is not { Length: > 0 and <= 60 } name) return Err(400, "Name is required (max 60 characters)");
            if (r.KeyId is null || !KeyIdRx.IsMatch(r.KeyId)) return Err(400, "Invalid API key ID");
            if (r.Secret is not { Length: >= 16 and <= 128 } || r.Secret.Any(char.IsWhiteSpace)) return Err(400, "Invalid API secret");
            if (!r.Paper && cfg["Brokers:AllowLive"] != "true") return Err(403, "Live trading is disabled. Set Brokers:AllowLive=true in the server configuration to connect a live account.");
            if (await db.Brokers.AnyAsync(x => x.KeyId == r.KeyId)) return Err(409, "This API key is already connected");
            var b = new Broker { Name = name, Paper = r.Paper, KeyId = r.KeyId, SecretProtected = hub.Protect(r.Secret) };
            try { b.LatencyMs = await hub.Gateway(b, r.Secret).PingMs(); }
            catch (BrokerException e) { return Err(400, e.Message); }
            db.Brokers.Add(b);
            await db.SaveChangesAsync();
            await sync.SyncBroker(b.Id);
            log.Add(b.Name, "Broker connected");
            var s = (await hub.Snapshots([b]))[0];
            return Results.Ok(BrokerDto(b, s.Account, s.Error, 0));
        });
        admin.MapPut("/brokers/{id}", async (string id, BrokerReq r, OtDb db, BrokerHub hub, EventLog log) =>
        {
            var b = await db.Brokers.FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Err(404, "Broker not found");
            if (r.Name?.Trim() is not { Length: > 0 and <= 60 } name) return Err(400, "Name is required (max 60 characters)");
            b.Name = name;
            if (!string.IsNullOrEmpty(r.Secret) || !string.IsNullOrEmpty(r.KeyId))
            {
                if (r.KeyId is null || !KeyIdRx.IsMatch(r.KeyId)) return Err(400, "Invalid API key ID");
                if (r.Secret is not { Length: >= 16 and <= 128 } || r.Secret.Any(char.IsWhiteSpace)) return Err(400, "Invalid API secret");
                b.KeyId = r.KeyId;
                b.SecretProtected = hub.Protect(r.Secret);
                try { b.LatencyMs = await hub.Gateway(b, r.Secret).PingMs(); }
                catch (BrokerException e) { return Err(400, e.Message); }
                b.LastError = null;
                hub.Invalidate(b.Id);
            }
            await db.SaveChangesAsync();
            log.Add(b.Name, "Broker updated");
            return Results.NoContent();
        });
        admin.MapDelete("/brokers/{id}", async (string id, OtDb db, BrokerHub hub, EventLog log) =>
        {
            var b = await db.Brokers.FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Err(404, "Broker not found");
            if (await db.Bots.AnyAsync(x => x.BrokerId == id)) return Err(409, "Remove or move this broker's bots first");
            db.ApiKeys.RemoveRange(db.ApiKeys.Where(k => k.BrokerId == id));
            db.Orders.RemoveRange(db.Orders.Where(o => o.BrokerId == id));
            db.Brokers.Remove(b);
            await db.SaveChangesAsync();
            hub.Invalidate(id);
            log.Add(b.Name, "Broker removed");
            return Results.NoContent();
        });
        trade.MapPost("/brokers/{id}/test", async (string id, OtDb db, BrokerHub hub, EventLog log) =>
        {
            var b = await db.Brokers.FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Err(404, "Broker not found");
            try
            {
                b.LatencyMs = await hub.Gateway(b).PingMs();
                b.LastError = null;
                await db.SaveChangesAsync();
                log.Add(b.Name, "Connection tested");
                return Results.Ok(new { ok = true, latencyMs = b.LatencyMs });
            }
            catch (BrokerException e)
            {
                b.LastError = e.Message;
                await db.SaveChangesAsync();
                log.Add(b.Name, "Connection test failed: " + e.Message, false);
                return Results.Ok(new { ok = false, error = e.Message });
            }
        });
        trade.MapPost("/brokers/{id}/sync", async (string id, OtDb db, BrokerHub hub, OrderSync sync) =>
        {
            if (!await db.Brokers.AnyAsync(x => x.Id == id)) return Err(404, "Broker not found");
            hub.Invalidate(id);
            await sync.SyncBroker(id);
            return Results.Ok(new { syncedAt = DateTimeOffset.UtcNow });
        });

        // ---------- bots ----------
        async Task<(Dictionary<string, decimal> Prices, List<Snapshot> Snaps)> Prices(OtDb db, BrokerHub hub, string? brokerId = null)
        {
            var brokers = await db.Brokers.AsNoTracking().Where(b => brokerId == null || b.Id == brokerId).ToListAsync();
            var snaps = await hub.Snapshots(brokers);
            return (snaps.SelectMany(s => s.Positions).GroupBy(p => p.Symbol).ToDictionary(g => g.Key, g => g.First().Last), snaps);
        }

        object BotDto(Bot b, Broker? br, BotStats st, int keys)
        {
            var online = b.LastHeartbeatMs is { } h && Clock.Now - h < 120_000;
            return new
            {
                b.Id, b.Name, b.Class, b.Description, b.BrokerId, b.ClientId, b.Running, b.Allocated, allocationPct = b.AllocPct, paper = br?.Paper ?? true, broker = br?.Name ?? "—",
                status = !b.Running ? "paused" : br?.Paper != false ? "simulated" : "running", online, heartbeat = b.LastHeartbeatMs, keys,
                invested = st.Invested, utilization = b.Allocated > 0 ? (double)Math.Min(1m, st.Invested / b.Allocated) : 0d,
                pnl = st.Pnl, realized = st.Realized, unrealized = st.Unrealized, returnPct = b.Allocated > 0 ? (double?)(st.Pnl / b.Allocated) : null,
            };
        }

        api.MapGet("/bots", async (OtDb db, BrokerHub hub) =>
        {
            var bots = await db.Bots.AsNoTracking().OrderBy(b => b.CreatedMs).ToListAsync();
            var brokers = await db.Brokers.AsNoTracking().ToDictionaryAsync(b => b.Id);
            var (prices, snaps) = await Prices(db, hub);
            var equity = Alloc.Equity(snaps);
            Alloc.Apply(bots, equity);
            var orders = (await db.Orders.AsNoTracking().Where(o => o.BotId != null && o.Filled > 0).ToListAsync()).GroupBy(o => o.BotId!).ToDictionary(g => g.Key, g => g.ToList());
            var keyCount = (await db.ApiKeys.AsNoTracking().Where(k => k.BotId != null).Select(k => k.BotId!).ToListAsync()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var rows = bots.Select(b => BotDto(b, brokers.GetValueOrDefault(b.BrokerId), Ledger.Compute(orders.GetValueOrDefault(b.Id) ?? [], prices), keyCount.GetValueOrDefault(b.Id))).ToList();
            var live = bots.Where(b => brokers.GetValueOrDefault(b.BrokerId)?.Paper == false).ToList();
            var alloc = bots.Sum(b => b.Allocated);
            var statsById = bots.ToDictionary(b => b.Id, b => Ledger.Compute(orders.GetValueOrDefault(b.Id) ?? [], prices));
            var invested = statsById.Values.Sum(s => s.Invested);
            var pnl = statsById.Values.Sum(s => s.Pnl);
            return Results.Ok(new
            {
                bots = rows,
                accounts = brokers.Values.Select(b => new { b.Id, b.Name, equity = equity.GetValueOrDefault(b.Id), bots = bots.Where(x => x.BrokerId == b.Id).Sum(x => x.AllocPct) }),
                summary = new
                {
                    running = bots.Count(b => b.Running), paused = bots.Count(b => !b.Running), live = live.Count, simulated = bots.Count - live.Count, backed = bots.Count(b => b.AllocPct > 0),
                    alloc, allocPct = bots.Sum(b => b.AllocPct), deployed = invested, util = alloc == 0 ? 0m : Math.Min(1m, invested / alloc), pnl, pnlPct = alloc == 0 ? 0m : pnl / alloc,
                },
            });
        });

        api.MapGet("/bots/{id}", async (string id, OtDb db, BrokerHub hub) =>
        {
            var b = await db.Bots.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Err(404, "Bot not found");
            var br = await db.Brokers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == b.BrokerId);
            var (prices, snaps) = await Prices(db, hub, b.BrokerId);
            Alloc.Apply([b], Alloc.Equity(snaps));
            var mine = await db.Orders.AsNoTracking().Where(o => o.BotId == id).OrderByDescending(o => o.CreatedMs).ToListAsync();
            var st = Ledger.Compute(mine, prices);
            var keys = await db.ApiKeys.AsNoTracking().Where(k => k.BotId == id).ToListAsync();
            var l = await Load(db);
            return Results.Ok(new
            {
                bot = BotDto(b, br, st, keys.Count),
                positions = st.Positions.Select(p => new { p.Symbol, p.Qty, p.Avg, p.Last, p.Value, p.Pnl }),
                orders = mine.Take(8).Select(o => OrderDto(o, l)),
                keys = keys.Select(k => new { k.Id, k.Name, hint = "••••" + k.Last4, lastUsed = k.LastUsedMs, perms = k.PermList }),
            });
        });

        (string? Error, string Name, string Class, string Desc, decimal Pct) CheckBot(BotReq r)
        {
            if (r.Name?.Trim() is not { Length: > 0 and <= 60 } name) return ("Name is required (max 60 characters)", "", "", "", 0);
            var cls = r.Class?.Trim() ?? "";
            var desc = r.Description?.Trim() ?? "";
            if (cls.Length > 40 || desc.Length > 200) return ("Class or description is too long", "", "", "", 0);
            if (r.AllocationPct is < 0 or > 100) return ("Allocation must be a percentage between 0 and 100", "", "", "", 0);
            return (null, name, cls.Length == 0 ? "General" : cls, desc, Math.Round(r.AllocationPct ?? 0, 2));
        }

        admin.MapPost("/bots", async (BotReq r, OtDb db) =>
        {
            var (err, name, cls, desc, pct) = CheckBot(r);
            if (err is not null) return Err(400, err);
            if (r.BrokerId is null || !await db.Brokers.AnyAsync(b => b.Id == r.BrokerId)) return Err(400, "Choose a broker account");
            var others = await db.Bots.AsNoTracking().Where(x => x.BrokerId == r.BrokerId).ToListAsync();
            var b = new Bot { Name = name, Class = cls, Description = desc, BrokerId = r.BrokerId, AllocPct = pct };
            if (Alloc.Check([.. others, b], x => x.AllocPct) is { } over) return Err(400, over);
            db.Bots.Add(b);
            await db.SaveChangesAsync();
            return Results.Ok(new { b.Id, b.ClientId });
        });
        admin.MapPut("/bots/allocations", async (AllocationsReq r, OtDb db) =>
        {
            if (r.Items is not { Length: > 0 and <= 200 }) return Err(400, "Send at least one bot");
            if (r.Items.Any(i => i.Id is null || i.Pct is null or < 0 or > 100)) return Err(400, "Each allocation needs a bot id and a percentage between 0 and 100");
            if (r.Items.Select(i => i.Id).Distinct().Count() != r.Items.Length) return Err(400, "A bot appears twice");
            var all = await db.Bots.ToListAsync();
            var wanted = r.Items.ToDictionary(i => i.Id!, i => Math.Round(i.Pct!.Value, 2));
            if (wanted.Keys.Any(id => all.All(b => b.Id != id))) return Err(404, "Bot not found");
            if (Alloc.Check(all, b => wanted.GetValueOrDefault(b.Id, b.AllocPct)) is { } over) return Err(400, over);
            foreach (var b in all.Where(b => wanted.ContainsKey(b.Id))) b.AllocPct = wanted[b.Id];
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        admin.MapPut("/bots/{id}", async (string id, BotReq r, OtDb db) =>
        {
            var b = await db.Bots.FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Err(404, "Bot not found");
            var (err, name, cls, desc, pct) = CheckBot(r);
            if (err is not null) return Err(400, err);
            var others = await db.Bots.AsNoTracking().Where(x => x.BrokerId == b.BrokerId && x.Id != id).ToListAsync();
            if (Alloc.Check([.. others, new Bot { BrokerId = b.BrokerId, AllocPct = pct }], x => x.AllocPct) is { } over) return Err(400, over);
            b.Name = name; b.Class = cls; b.Description = desc; b.AllocPct = pct;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        admin.MapDelete("/bots/{id}", async (string id, OtDb db) =>
        {
            var b = await db.Bots.FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Err(404, "Bot not found");
            db.ApiKeys.RemoveRange(db.ApiKeys.Where(k => k.BotId == id));
            await foreach (var o in db.Orders.Where(o => o.BotId == id).AsAsyncEnumerable()) o.BotId = null;
            db.Bots.Remove(b);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        async Task<IResult> SetRunning(string id, bool run, OtDb db)
        {
            var b = await db.Bots.FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Err(404, "Bot not found");
            b.Running = run;
            b.PausedMs = run ? null : Clock.Now;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }
        trade.MapPost("/bots/{id}/pause", (string id, OtDb db) => SetRunning(id, false, db));
        trade.MapPost("/bots/{id}/resume", (string id, OtDb db) => SetRunning(id, true, db));

        // ---------- api keys (admin) ----------
        object KeyDto(ApiKey k, Dictionary<string, Broker> brokers, Dictionary<string, Bot> bots) => new
        {
            k.Id, k.Name, k.BrokerId, broker = brokers.GetValueOrDefault(k.BrokerId)?.Name ?? "—", paper = brokers.GetValueOrDefault(k.BrokerId)?.Paper ?? true,
            k.BotId, bot = k.BotId is null ? null : bots.GetValueOrDefault(k.BotId)?.Name, perms = k.PermList, hint = "••••" + k.Last4,
            lastUsed = k.LastUsedMs, expires = k.ExpiresMs, requests = k.Requests, expired = k.ExpiresMs < Clock.Now,
        };

        admin.MapGet("/keys", async (OtDb db) =>
        {
            var l = await Load(db);
            var keys = await db.ApiKeys.AsNoTracking().OrderBy(k => k.CreatedMs).ToListAsync();
            return Results.Ok(new
            {
                keys = keys.Select(k => KeyDto(k, l.Brokers, l.Bots)),
                requests = keys.Sum(k => k.Requests), active = keys.Count(k => k.ExpiresMs >= Clock.Now),
                lastUsed = keys.Max(k => k.LastUsedMs), rateLimit = Program.V1RateLimit,
            });
        });
        admin.MapPost("/keys", async (KeyReq r, OtDb db) =>
        {
            if (r.Name?.Trim() is not { Length: > 0 and <= 60 } name) return Err(400, "Name is required (max 60 characters)");
            if (r.Perms is not { Length: > 0 and <= 3 } || r.Perms.Any(p => !AllowedPerms.Contains(p))) return Err(400, "Choose at least one valid permission");
            var broker = r.BrokerId is null ? null : await db.Brokers.AsNoTracking().FirstOrDefaultAsync(b => b.Id == r.BrokerId);
            if (broker is null) return Err(400, "Choose a broker account");
            if (!string.IsNullOrEmpty(r.BotId) && !await db.Bots.AnyAsync(b => b.Id == r.BotId && b.BrokerId == broker.Id)) return Err(400, "That bot does not belong to this broker account");
            if (await db.ApiKeys.CountAsync() >= 50) return Err(409, "Key limit reached");
            var days = Math.Clamp(r.ExpiresInDays ?? 90, 1, 365);
            var (secret, hash, last4) = NewSecret(broker.Paper);
            var k = new ApiKey
            {
                Name = name, BrokerId = broker.Id, BotId = string.IsNullOrEmpty(r.BotId) ? null : r.BotId, Perms = string.Join(',', r.Perms.Distinct()),
                Hash = hash, Last4 = last4, ExpiresMs = DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeMilliseconds(),
            };
            db.ApiKeys.Add(k);
            await db.SaveChangesAsync();
            var l = await Load(db);
            return Results.Ok(new { key = KeyDto(k, l.Brokers, l.Bots), secret });
        });
        admin.MapPost("/keys/{id}/rotate", async (string id, OtDb db) =>
        {
            var k = await db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id);
            if (k is null) return Err(404, "Key not found");
            var paper = (await db.Brokers.AsNoTracking().FirstOrDefaultAsync(b => b.Id == k.BrokerId))?.Paper ?? true;
            var (secret, hash, last4) = NewSecret(paper);
            k.Hash = hash; k.Last4 = last4; k.ExpiresMs = DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
            var l = await Load(db);
            return Results.Ok(new { key = KeyDto(k, l.Brokers, l.Bots), secret });
        });
        admin.MapDelete("/keys/{id}", async (string id, OtDb db) =>
        {
            var k = await db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id);
            if (k is null) return Err(404, "Key not found");
            db.ApiKeys.Remove(k);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>Only the SHA-256 of a key is stored; the secret itself is shown once.</summary>
    public static (string Secret, byte[] Hash, string Last4) NewSecret(bool paper)
    {
        var s = (paper ? "ot_test_" : "ot_live_") + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (s, HashKey(s), s[^4..]);
    }

    public static byte[] HashKey(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}
