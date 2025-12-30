namespace IAMS.Shared.DTOs;

public record TenantDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? LogoUrl { get; init; }
    public string? PrimaryColor { get; init; }

    // Subscription
    public required string SubscriptionTier { get; init; }
    public DateTime SubscriptionStartDate { get; init; }
    public DateTime? SubscriptionEndDate { get; init; }
    public bool IsActive { get; init; }

    // Limits
    public int MaxAssets { get; init; }
    public int MaxUsers { get; init; }
    public long MaxStorageBytes { get; init; }

    // Current Usage
    public int CurrentAssetCount { get; init; }
    public int CurrentUserCount { get; init; }
    public long CurrentStorageBytes { get; init; }

    public DateTime CreatedAt { get; init; }
}

public record TenantSummaryDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? LogoUrl { get; init; }
    public string? PrimaryColor { get; init; }
    public required string SubscriptionTier { get; init; }
    public bool IsActive { get; init; }
}

public record CreateTenantDto
{
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? LogoUrl { get; init; }
    public string? PrimaryColor { get; init; }
    public string SubscriptionTier { get; init; } = "Free";

    // Initial admin user
    public required string AdminEmail { get; init; }
    public required string AdminPassword { get; init; }
    public required string AdminFullName { get; init; }
}

public record UpdateTenantDto
{
    public string? Name { get; init; }
    public string? LogoUrl { get; init; }
    public string? PrimaryColor { get; init; }
    public string? SubscriptionTier { get; init; }
    public DateTime? SubscriptionEndDate { get; init; }
    public bool? IsActive { get; init; }
}

public record TenantUsageDto
{
    public Guid TenantId { get; init; }
    public required string TenantName { get; init; }

    // Current Usage
    public int CurrentAssetCount { get; init; }
    public int CurrentUserCount { get; init; }
    public long CurrentStorageBytes { get; init; }

    // Limits
    public int MaxAssets { get; init; }
    public int MaxUsers { get; init; }
    public long MaxStorageBytes { get; init; }

    // Percentages
    public double AssetUsagePercent => MaxAssets > 0 ? (double)CurrentAssetCount / MaxAssets * 100 : 0;
    public double UserUsagePercent => MaxUsers > 0 ? (double)CurrentUserCount / MaxUsers * 100 : 0;
    public double StorageUsagePercent => MaxStorageBytes > 0 ? (double)CurrentStorageBytes / MaxStorageBytes * 100 : 0;
}
