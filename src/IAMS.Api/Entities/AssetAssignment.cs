namespace IAMS.Api.Entities;

/// <summary>
/// Tracks the assignment history of assets to users (audit trail)
/// </summary>
public class AssetAssignment
{
    public int Id { get; set; }

    // Asset being assigned
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    // User the asset is assigned to
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    // Assignment details
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReturnedAt { get; set; }

    // Who performed the action
    public string AssignedByUserId { get; set; } = null!;
    public ApplicationUser AssignedByUser { get; set; } = null!;

    public string? ReturnedByUserId { get; set; }
    public ApplicationUser? ReturnedByUser { get; set; }

    // Additional context
    public string? Notes { get; set; }
    public string? ReturnNotes { get; set; }
    public string? ReturnCondition { get; set; } // Good, Damaged, Lost

    // Computed properties
    public bool IsActive => ReturnedAt is null;
    public TimeSpan? Duration => ReturnedAt.HasValue
        ? ReturnedAt.Value - AssignedAt
        : DateTime.UtcNow - AssignedAt;
}

public static class ReturnCondition
{
    public const string Good = "Good";
    public const string Damaged = "Damaged";
    public const string Lost = "Lost";
    public const string NeedsRepair = "NeedsRepair";

    public static readonly string[] All = [Good, Damaged, Lost, NeedsRepair];
}
