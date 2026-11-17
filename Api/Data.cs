using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace OpenTrade.Api;

public enum Role { Admin, Trader, Viewer }
public enum Side { Buy, Sell }
public enum OrderType { Limit, Market }
public enum OrderStatus { Pending, Partial, Filled, Cancelled, Rejected }

public static class Clock
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public Role Role { get; set; } = Role.Viewer;
    public byte[] Salt { get; set; } = [];
    public byte[] Hash { get; set; } = [];
    public bool Active { get; set; } = true;
    public long CreatedMs { get; set; } = Clock.Now;
}

/// <summary>A connected broker account (Alpaca). The API secret is stored encrypted with ASP.NET Data Protection.</summary>
public class Broker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "alpaca";
    public bool Paper { get; set; } = true;
    public string KeyId { get; set; } = "";
    public string SecretProtected { get; set; } = "";
    public long CreatedMs { get; set; } = Clock.Now;
    public long? LastSyncMs { get; set; }
    public int LatencyMs { get; set; }
    public string? LastError { get; set; }
}

public class Bot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Description { get; set; } = "";
    public string BrokerId { get; set; } = "";
    public string ClientId { get; set; } = "bot_" + Guid.NewGuid().ToString("N")[..10];
    /// <summary>Share of the broker account's equity this bot may use (0-100). The sum over a broker's bots never exceeds 100.</summary>
    public decimal AllocPct { get; set; }
    /// <summary>USD value of <see cref="AllocPct"/> at the current equity. Computed on every read, never stored.</summary>
    [NotMapped] public decimal Allocated { get; set; }
    public bool Running { get; set; } = true;
    public long CreatedMs { get; set; } = Clock.Now;
    public long? PausedMs { get; set; }
    public long? LastHeartbeatMs { get; set; }
}

public class Order
{
    public string Id { get; set; } = "OT-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    public string BrokerOrderId { get; set; } = "";
    public string BrokerId { get; set; } = "";
    public string? BotId { get; set; }
    public string Symbol { get; set; } = "";
    public Side Side { get; set; }
    public OrderType Type { get; set; }
    public string Tif { get; set; } = "day";
    public decimal Qty { get; set; }
    public decimal Filled { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? AvgFill { get; set; }
    public OrderStatus Status { get; set; }
    /// <summary>ui = placed from the dashboard, api = placed by a bot through an API key, external = found at the broker.</summary>
    public string Source { get; set; } = "ui";
    public long CreatedMs { get; set; }
    public long UpdatedMs { get; set; }

    public bool Open => Status is OrderStatus.Pending or OrderStatus.Partial;
    public decimal Price => AvgFill ?? LimitPrice ?? 0m;
}

public class ApiKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BrokerId { get; set; } = "";
    public string? BotId { get; set; }
    public string Perms { get; set; } = "read";
    public byte[] Hash { get; set; } = [];
    public string Last4 { get; set; } = "";
    public long CreatedMs { get; set; } = Clock.Now;
    public long? LastUsedMs { get; set; }
    public long ExpiresMs { get; set; }
    public long Requests { get; set; }

    public string[] PermList => Perms.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

public class OtDb(DbContextOptions<OtDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Broker> Brokers => Set<Broker>();
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<User>(e => { e.HasIndex(x => x.Email).IsUnique(); e.Property(x => x.Role).HasConversion<string>(); });
        m.Entity<Bot>(e => e.HasIndex(x => x.ClientId).IsUnique());
        m.Entity<Order>(e =>
        {
            e.HasIndex(x => x.BrokerOrderId).IsUnique();
            e.HasIndex(x => x.CreatedMs);
            e.Property(x => x.Side).HasConversion<string>();
            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
        });
        m.Entity<ApiKey>(e => e.HasIndex(x => x.Hash).IsUnique());
    }
}
