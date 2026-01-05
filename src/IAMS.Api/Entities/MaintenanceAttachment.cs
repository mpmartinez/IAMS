namespace IAMS.Api.Entities;

/// <summary>
/// Represents a file attachment associated with a maintenance record
/// </summary>
public class MaintenanceAttachment : ITenantEntity
{
    public int Id { get; set; }

    // Multi-tenancy
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    // Maintenance reference
    public int MaintenanceId { get; set; }
    public Maintenance Maintenance { get; set; } = null!;

    // File information
    public required string FileName { get; set; }
    public required string StoredFileName { get; set; } // GUID-based name for storage
    public required string ContentType { get; set; }
    public long FileSizeBytes { get; set; }

    // Categorization
    public required string Category { get; set; } // BeforePhoto, AfterPhoto, Receipt, Document, Other

    // Optional description
    public string? Description { get; set; }

    // Audit fields
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public required string UploadedByUserId { get; set; }
    public ApplicationUser UploadedByUser { get; set; } = null!;
}

/// <summary>
/// Predefined maintenance attachment categories
/// </summary>
public static class MaintenanceAttachmentCategories
{
    public const string BeforePhoto = "BeforePhoto";
    public const string AfterPhoto = "AfterPhoto";
    public const string Receipt = "Receipt";
    public const string Document = "Document";
    public const string Other = "Other";

    public static readonly string[] All = [BeforePhoto, AfterPhoto, Receipt, Document, Other];

    public static bool IsValid(string category) => All.Contains(category);
}
