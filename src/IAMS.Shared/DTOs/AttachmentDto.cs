using System.ComponentModel.DataAnnotations;

namespace IAMS.Shared.DTOs;

/// <summary>
/// Represents an attachment record
/// </summary>
public record AttachmentDto
{
    public int Id { get; init; }
    public int AssetId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long FileSizeBytes { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public required string UploadedByUserId { get; init; }
    public required string UploadedByUserName { get; init; }

    /// <summary>
    /// Human-readable file size display
    /// </summary>
    public string FileSizeDisplay => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
        _ => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB"
    };

    /// <summary>
    /// Whether this attachment is an image that can be previewed
    /// </summary>
    public bool IsImage => ContentType.StartsWith("image/");
}

/// <summary>
/// Request to upload an attachment
/// </summary>
public record CreateAttachmentDto
{
    [Required(ErrorMessage = "Category is required")]
    [StringLength(50, ErrorMessage = "Category cannot exceed 50 characters")]
    public required string Category { get; init; }

    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }
}

/// <summary>
/// Summary of attachments for an asset
/// </summary>
public record AttachmentSummaryDto
{
    public int TotalCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public int ReceiptCount { get; init; }
    public int PhotoCount { get; init; }
    public int WarrantyDocumentCount { get; init; }
    public int ManualCount { get; init; }
    public int OtherCount { get; init; }

    /// <summary>
    /// Human-readable total size display
    /// </summary>
    public string TotalSizeDisplay => TotalSizeBytes switch
    {
        < 1024 => $"{TotalSizeBytes} B",
        < 1024 * 1024 => $"{TotalSizeBytes / 1024.0:F1} KB",
        _ => $"{TotalSizeBytes / (1024.0 * 1024.0):F2} MB"
    };
}
