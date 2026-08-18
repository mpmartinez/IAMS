namespace AssetDesk.Api.Entities;

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
    public int MaxTicketsPerMonth { get; set; }

    // Current Usage (updated periodically)
    public int CurrentAssetCount { get; set; }
    public int CurrentUserCount { get; set; }
    public long CurrentStorageBytes { get; set; }

    // Audit
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// When SeedData.EnsureRolePermissionsAsync last provisioned this tenant's built-in role
    /// grants. Null means "never provisioned" - the tenant is still eligible for the initial
    /// backfill. Non-null means provisioning has already happened, so a role holding zero grants
    /// is a deliberate revocation, not a state to repair. See the doc comment on
    /// EnsureRolePermissionsAsync for why a per-role "has any grant row" check could not tell the
    /// two apart.
    /// </summary>
    public DateTime? RolePermissionsSeededAt { get; set; }

    // Navigation
    public ICollection<ApplicationUser> Users { get; set; } = [];
}

public static class SubscriptionTiers
{
    public const string Free = "Free";
    public const string Pro = "Pro";
    public const string Enterprise = "Enterprise";

    public static readonly string[] All = [Free, Pro, Enterprise];

    public static (int MaxAssets, int MaxUsers, long MaxStorageBytes, int MaxTicketsPerMonth) GetLimits(string tier) => tier switch
    {
        Free => (50, 5, 100L * 1024 * 1024, 100),               // 50 assets, 5 users, 100MB, 100 tickets/mo
        Pro => (500, 25, 1024L * 1024 * 1024, 1000),            // 500 assets, 25 users, 1GB, 1000 tickets/mo
        Enterprise => (10000, 500, 50L * 1024 * 1024 * 1024, int.MaxValue), // 10K assets, 500 users, 50GB, unlimited tickets
        _ => (50, 5, 100L * 1024 * 1024, 100)
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
            MaxStorageBytes = limits.MaxStorageBytes,
            MaxTicketsPerMonth = limits.MaxTicketsPerMonth
        };
    }
}
