namespace IAMS.Api.Entities;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }

    // Subscription
    public required string SubscriptionTier { get; set; }
    public DateTime SubscriptionStartDate { get; set; } = DateTime.UtcNow;
    public DateTime? SubscriptionEndDate { get; set; }
    public bool IsActive { get; set; } = true;

    // Usage Limits
    public int MaxAssets { get; set; }
    public int MaxUsers { get; set; }
    public long MaxStorageBytes { get; set; }

    // Current Usage (updated periodically)
    public int CurrentAssetCount { get; set; }
    public int CurrentUserCount { get; set; }
    public long CurrentStorageBytes { get; set; }

    // Audit
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    // Navigation
    public ICollection<ApplicationUser> Users { get; set; } = [];
}

public static class SubscriptionTiers
{
    public const string Free = "Free";
    public const string Pro = "Pro";
    public const string Enterprise = "Enterprise";

    public static readonly string[] All = [Free, Pro, Enterprise];

    public static (int MaxAssets, int MaxUsers, long MaxStorageBytes) GetLimits(string tier) => tier switch
    {
        Free => (50, 5, 100L * 1024 * 1024),              // 50 assets, 5 users, 100MB
        Pro => (500, 25, 1024L * 1024 * 1024),            // 500 assets, 25 users, 1GB
        Enterprise => (10000, 500, 50L * 1024 * 1024 * 1024), // 10K assets, 500 users, 50GB
        _ => (50, 5, 100L * 1024 * 1024)
    };

    public static Tenant CreateWithLimits(string name, string slug, string tier)
    {
        var limits = GetLimits(tier);
        return new Tenant
        {
            Name = name,
            Slug = slug.ToLowerInvariant(),
            SubscriptionTier = tier,
            MaxAssets = limits.MaxAssets,
            MaxUsers = limits.MaxUsers,
            MaxStorageBytes = limits.MaxStorageBytes
        };
    }
}
