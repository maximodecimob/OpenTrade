using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTrade.Api;

var b = WebApplication.CreateBuilder(args);
b.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var dataDir = b.Configuration["Data:Dir"] ?? Path.Combine(b.Environment.ContentRootPath, "data");
// One-time move: older versions kept the data in Api/data. Moving it keeps users, bots and broker connections.
var oldDir = Path.Combine(b.Environment.ContentRootPath, "Api", "data");
if (b.Configuration["Data:Dir"] is null && !Directory.Exists(dataDir) && Directory.Exists(oldDir)) Directory.Move(oldDir, dataDir);
Directory.CreateDirectory(dataDir);

b.Services.AddDbContext<OtDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "opentrade.db")}"));
var dp = b.Services.AddDataProtection().SetApplicationName("OpenTrade").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(); // broker secrets are encrypted with these keys, so the key ring itself is encrypted for this Windows user
b.Services.AddMemoryCache();
b.Services.AddHttpClient("alpaca", c => c.Timeout = TimeSpan.FromSeconds(15));
b.Services.AddSingleton<IBrokerFactory, AlpacaFactory>();
b.Services.AddSingleton<BrokerHub>();
b.Services.AddSingleton<EventLog>();
b.Services.AddSingleton<OrderSync>();
b.Services.AddHostedService(sp => sp.GetRequiredService<OrderSync>());
b.Services.AddScoped<OrderService>();
b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
b.Services.AddResponseCompression(o => o.EnableForHttps = true);
b.Services.AddAuthorization();
b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "ot.sid";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = b.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    // Every request re-checks the account, so disabling a user or changing a role takes effect immediately.
    o.Events.OnValidatePrincipal = async c =>
    {
        var id = c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var u = id is null ? null : await c.HttpContext.RequestServices.GetRequiredService<OtDb>().Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (u is null || !u.Active) { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); return; }
        var cur = c.Principal!;
        if (cur.FindFirstValue(ClaimTypes.Role) != u.Role.ToString() || cur.FindFirstValue(ClaimTypes.Name) != u.Name)
        {
            c.ReplacePrincipal(Endpoints.Principal(u));
            c.ShouldRenew = true;
        }
    };
});
var loginLimit = b.Configuration.GetValue("RateLimit:LoginPerMinute", 5);
b.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new() { PermitLimit = loginLimit, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("v1", c => RateLimitPartition.GetFixedWindowLimiter(PublicApi.RateKey(c), _ => new() { PermitLimit = Program.V1RateLimit, Window = TimeSpan.FromMinutes(1) }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new() { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
});

var app = b.Build();

// ---- database + first admin ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OtDb>();
    db.Database.EnsureCreated();
    SchemaUpgrade.Run(db);
    if (!db.Users.Any())
    {
        // No credentials live in the repository. The admin comes from configuration (Admin:Email + Admin:Password, e.g. environment variables)
        // or, when nothing is configured, the first account created from the web page or POST /api/register becomes the admin.
        var password = app.Configuration["Admin:Password"];
        if (string.IsNullOrEmpty(password))
            Console.WriteLine($"[first start] no users yet. Open {app.Configuration["Urls"] ?? "http://localhost:5080"} and click \"Create account\": the first account becomes the administrator.");
        else
        {
            if (Passwords.Validate(password) is { } bad) throw new InvalidOperationException("Admin:Password: " + bad);
            var email = (app.Configuration["Admin:Email"] ?? "admin@admin.com").Trim().ToLowerInvariant();
            var u = new User { Email = email, Name = app.Configuration["Admin:Name"] ?? "Administrator", Role = Role.Admin };
            (u.Salt, u.Hash) = Passwords.Create(password);
            db.Users.Add(u);
            db.SaveChanges();
            Console.WriteLine($"[first start] admin account created -> {email} (password from Admin:Password)");
        }
    }
}

// Only loopback proxies are trusted by default, so a client cannot fake its IP (rate limits) with X-Forwarded-For.
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.UseResponseCompression();
app.Use(async (c, next) =>
{
    var h = c.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (!app.Environment.IsDevelopment()) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    // CSRF: browsers can't add custom headers cross-site without a CORS preflight, which we never approve.
    if (c.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(c.Request.Method) && c.Request.Headers["X-OT"] != "1")
    { c.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (BrokerException e) when (!c.Response.HasStarted)
    {
        c.Response.StatusCode = e.Status switch { 422 => 409, 404 => 404, _ => 502 };
        await c.Response.WriteAsJsonAsync(new { error = e.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
        c.File.PhysicalPath?.Contains($"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}") == true ? "public,max-age=31536000,immutable" : "no-cache"
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAppApi();
app.MapPublicApi();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program
{
    public const int V1RateLimit = 120;
}

/// <summary>Small, additive schema upgrades. The database is created with EnsureCreated (no migrations), so older files need a nudge.</summary>
static class SchemaUpgrade
{
    public static void Run(OtDb db)
    {
        db.Database.OpenConnection();
        try
        {
            var cols = new List<string>();
            using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(Bots)";
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) cols.Add(rd.GetString(1));
            }
            // v1 stored a fixed USD amount (Allocated); v2 stores a percentage of the account (AllocPct). Old amounts cannot be converted without the equity, so they restart at 0%.
            if (!cols.Contains("AllocPct")) db.Database.ExecuteSqlRaw("ALTER TABLE Bots ADD COLUMN AllocPct TEXT NOT NULL DEFAULT '0'");
            if (cols.Contains("Allocated")) db.Database.ExecuteSqlRaw("ALTER TABLE Bots DROP COLUMN Allocated");
        }
        finally { db.Database.CloseConnection(); }
    }
}
