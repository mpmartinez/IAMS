namespace IAMS.Api.Entities;

/// <summary>
/// Represents a file attachment associated with an asset
/// </summary>
public class Attachment
{
    public int Id { get; set; }

    // Asset relationship
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    // File information
    public required string FileName { get; set; }
    public required string StoredFileName { get; set; } // GUID-based name for storage
    public required string ContentType { get; set; }
    public long FileSizeBytes { get; set; }

    // Categorization
    public required string Category { get; set; } // Receipt, Photo, WarrantyDocument, Manual, Other

    // Optional description
    public string? Description { get; set; }

    // Audit fields
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public required string UploadedByUserId { get; set; }
    public ApplicationUser UploadedByUser { get; set; } = null!;
}

/// <summary>
/// Predefined attachment categories
/// </summary>
public static class AttachmentCategories
{
    public const string Receipt = "Receipt";
    public const string Photo = "Photo";
    public const string WarrantyDocument = "WarrantyDocument";
    public const string Manual = "Manual";
    public const string Other = "Other";

    public static readonly string[] All = [Receipt, Photo, WarrantyDocument, Manual, Other];
}
