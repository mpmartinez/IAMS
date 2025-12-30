namespace IAMS.Api.Entities;

public class Notification : ITenantEntity
{
    public int Id { get; set; }

    // Multi-tenancy
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public required string UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public required string Title { get; set; }
    public required string Message { get; set; }
    public required string Type { get; set; } // Info, Warning, Error, Success
    public string? Link { get; set; } // Optional navigation link
    public string? RelatedEntityType { get; set; } // Asset, WarrantyAlert, etc.
    public int? RelatedEntityId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public static class NotificationTypes
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Success = "Success";

    public static readonly string[] All = [Info, Warning, Error, Success];
}
