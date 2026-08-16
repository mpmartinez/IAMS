namespace IAMS.Api.Entities;

/// <summary>
/// Append-only record of changes to audited entities. Never updated or deleted.
/// </summary>
public class AuditLog : ITenantEntity
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? UserId { get; set; }

    /// <summary>JSON object of shape { "Field": { "from": ..., "to": ... } }. Null for Created and Deleted.</summary>
    public string? Changes { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public static class AuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
}
