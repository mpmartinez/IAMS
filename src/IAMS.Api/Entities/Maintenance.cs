namespace IAMS.Api.Entities;

/// <summary>
/// Represents a maintenance record for an asset
/// </summary>
public class Maintenance : ITenantEntity
{
    public int Id { get; set; }

    // Multi-tenancy
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    // Asset reference
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    // Maintenance details
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = MaintenanceStatus.Pending;
    public string? Notes { get; set; }

    // Dates
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Who performed/requested
    public string CreatedByUserId { get; set; } = "";
    public ApplicationUser? CreatedByUser { get; set; }
    public string? PerformedByUserId { get; set; }
    public ApplicationUser? PerformedByUser { get; set; }

    // Navigation for attachments
    public ICollection<MaintenanceAttachment> Attachments { get; set; } = [];
}

/// <summary>
/// Predefined maintenance statuses
/// </summary>
public static class MaintenanceStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly string[] All = [Pending, InProgress, Completed, Cancelled];

    public static bool IsValid(string status) => All.Contains(status);
}
