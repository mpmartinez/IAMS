namespace IAMS.Api.Entities;

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
