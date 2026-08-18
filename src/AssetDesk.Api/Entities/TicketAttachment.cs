namespace AssetDesk.Api.Entities;

public class TicketAttachment : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    public required string FileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public required string Category { get; set; }
    public string? Description { get; set; }

    // Required, matching the deleted MaintenanceAttachment: an audit feature must
    // always record who uploaded a file. Keeping it non-null also spares the
    // migration an ALTER COLUMN on an existing NOT NULL column.
    public required string UploadedByUserId { get; set; }
    public ApplicationUser? UploadedByUser { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Predefined ticket attachment categories. Mirrors the deleted
/// MaintenanceAttachmentCategories 1:1 - see TicketAttachmentsController.
/// </summary>
public static class TicketAttachmentCategories
{
    public const string BeforePhoto = "BeforePhoto";
    public const string AfterPhoto = "AfterPhoto";
    public const string Receipt = "Receipt";
    public const string Document = "Document";
    public const string Other = "Other";

    public static readonly string[] All = [BeforePhoto, AfterPhoto, Receipt, Document, Other];

    public static bool IsValid(string category) => All.Contains(category);
}
