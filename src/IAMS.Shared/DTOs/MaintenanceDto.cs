using System.ComponentModel.DataAnnotations;

namespace IAMS.Shared.DTOs;

/// <summary>
/// Represents a maintenance record
/// </summary>
public record MaintenanceDto
{
    public int Id { get; init; }
    public int AssetId { get; init; }
    public string AssetTag { get; init; } = "";
    public string AssetName { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string Status { get; init; } = "";
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string CreatedByUserId { get; init; } = "";
    public string CreatedByUserName { get; init; } = "";
    public string? PerformedByUserId { get; init; }
    public string? PerformedByUserName { get; init; }
    public int AttachmentCount { get; init; }

    /// <summary>
    /// Whether this maintenance is still active (Pending or InProgress)
    /// </summary>
    public bool IsActive => Status is "Pending" or "InProgress";
}

/// <summary>
/// Request to create a new maintenance record
/// </summary>
public class CreateMaintenanceDto
{
    [Required(ErrorMessage = "Asset ID is required")]
    public int AssetId { get; set; }

    [Required(ErrorMessage = "Title is required")]
    [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters")]
    public string Title { get; set; } = "";

    [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
    public string? Description { get; set; }
}

/// <summary>
/// Request to update a maintenance record
/// </summary>
public record UpdateMaintenanceDto
{
    [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters")]
    public string? Title { get; init; }

    [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
    public string? Description { get; init; }

    [StringLength(50, ErrorMessage = "Status cannot exceed 50 characters")]
    public string? Status { get; init; }

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters")]
    public string? Notes { get; init; }

    public string? PerformedByUserId { get; init; }
}

/// <summary>
/// Represents a maintenance attachment record
/// </summary>
public record MaintenanceAttachmentDto
{
    public int Id { get; init; }
    public int MaintenanceId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long FileSizeBytes { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public required string UploadedByUserId { get; init; }
    public required string UploadedByUserName { get; init; }

    /// <summary>
    /// Whether this attachment is an image that can be previewed
    /// </summary>
    public bool IsImage => ContentType.StartsWith("image/");

    /// <summary>
    /// Human-readable file size display
    /// </summary>
    public string FileSizeDisplay => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
        _ => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB"
    };
}

/// <summary>
/// Summary statistics for maintenance records
/// </summary>
public record MaintenanceSummaryDto
{
    public int TotalCount { get; init; }
    public int PendingCount { get; init; }
    public int InProgressCount { get; init; }
    public int CompletedCount { get; init; }
    public int CancelledCount { get; init; }
}
