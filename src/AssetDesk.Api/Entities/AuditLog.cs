namespace AssetDesk.Api.Entities;

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

    /// An administrator sent a password reset link for another user. Recorded explicitly
    /// because ApplicationUser is deliberately absent from AuditSaveChangesInterceptor's
    /// AuditedTypes - auditing it wholesale would serialise PasswordHash and SecurityStamp
    /// into Changes on every edit.
    public const string PasswordResetSent = "PasswordResetSent";

    /// Someone read a licence key in full. Written explicitly by the reveal endpoint: reading a key
    /// changes no row, so the automatic change log would never see it.
    public const string LicenceKeyRevealed = "LicenceKeyRevealed";
}
