namespace IAMS.Shared.DTOs;

/// <summary>
/// Represents a ticket attachment record. Mirrors the deleted MaintenanceAttachmentDto,
/// except the timestamp is UploadedAt - matching the TicketAttachment entity's property
/// name, rather than the old CreatedAt name the deleted DTO used.
/// </summary>
public record TicketAttachmentDto
{
    public int Id { get; init; }
    public int TicketId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long FileSizeBytes { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }
    public DateTime UploadedAt { get; init; }
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
