using System.ComponentModel.DataAnnotations;

namespace IAMS.Shared.DTOs;

/// <summary>
/// Represents an asset assignment record
/// </summary>
public record AssetAssignmentDto
{
    public int Id { get; init; }
    public int AssetId { get; init; }
    public string AssetTag { get; init; } = "";
    public string AssetDisplayName { get; init; } = "";
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string? UserDepartment { get; init; }
    public DateTime AssignedAt { get; init; }
    public DateTime? ReturnedAt { get; init; }
    public string AssignedByUserId { get; init; } = "";
    public string AssignedByUserName { get; init; } = "";
    public string? ReturnedByUserId { get; init; }
    public string? ReturnedByUserName { get; init; }
    public string? Notes { get; init; }
    public string? ReturnNotes { get; init; }
    public string? ReturnCondition { get; init; }

    // Computed
    public bool IsActive => ReturnedAt is null;
    public int DurationDays => ReturnedAt.HasValue
        ? (int)(ReturnedAt.Value - AssignedAt).TotalDays
        : (int)(DateTime.UtcNow - AssignedAt).TotalDays;
}

/// <summary>
/// Request to assign an asset to a user
/// </summary>
public record AssignAssetRequest
{
    [Required(ErrorMessage = "User ID is required")]
    public required string UserId { get; init; }

    [StringLength(1000, ErrorMessage = "Notes cannot exceed 1000 characters")]
    public string? Notes { get; init; }
}

/// <summary>
/// Request to return an asset from a user
/// </summary>
public record ReturnAssetRequest
{
    [StringLength(1000, ErrorMessage = "Notes cannot exceed 1000 characters")]
    public string? Notes { get; init; }

    [StringLength(50, ErrorMessage = "Return condition cannot exceed 50 characters")]
    public string? ReturnCondition { get; init; } // Good, Damaged, Lost, NeedsRepair
}

/// <summary>
/// Summary of assets assigned to a user
/// </summary>
public record UserAssetsDto
{
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string? Department { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public List<AssetDto> CurrentAssets { get; init; } = [];
    public int TotalCurrentAssets { get; init; }
    public decimal TotalAssetValue { get; init; }
    public int TotalPastAssignments { get; init; }
}

/// <summary>
/// Offboarding summary for a departing user
/// </summary>
public record OffboardingDto
{
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string? Department { get; init; }
    public string? Email { get; init; }
    public List<UnreturnedAssetDto> UnreturnedAssets { get; init; } = [];
    public int TotalUnreturnedAssets { get; init; }
    public decimal TotalUnreturnedValue { get; init; }
    public bool IsReadyForOffboarding => TotalUnreturnedAssets == 0;
}

/// <summary>
/// Unreturned asset details for offboarding
/// </summary>
public record UnreturnedAssetDto
{
    public int AssetId { get; init; }
    public string AssetTag { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string DeviceType { get; init; } = "";
    public string? SerialNumber { get; init; }
    public decimal? PurchasePrice { get; init; }
    public string Currency { get; init; } = "USD";
    public DateTime AssignedAt { get; init; }
    public int DaysAssigned { get; init; }
    public string? Location { get; init; }
}

/// <summary>
/// Bulk return result for offboarding
/// </summary>
public record BulkReturnRequest
{
    [Required(ErrorMessage = "At least one asset ID is required")]
    public required List<int> AssetIds { get; init; }

    [StringLength(1000, ErrorMessage = "Notes cannot exceed 1000 characters")]
    public string? Notes { get; init; }

    [StringLength(50, ErrorMessage = "Return condition cannot exceed 50 characters")]
    public string ReturnCondition { get; init; } = "Good";
}

public record BulkReturnResult
{
    public int TotalRequested { get; init; }
    public int TotalReturned { get; init; }
    public List<string> Errors { get; init; } = [];
}
