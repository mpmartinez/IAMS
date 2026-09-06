namespace AssetDesk.Shared.DTOs;

/// <summary>
/// One entry in the audit trail, as read by /api/audit.
/// </summary>
public class AuditLogDto
{
    public long Id { get; set; }

    /// <summary>Only meaningful to a super-admin, whose reads span every tenant.</summary>
    public Guid TenantId { get; set; }

    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";

    public string? UserId { get; set; }

    /// <summary>Resolved from UserId at read time. "System" when the id is null, "Unknown user"
    /// when it no longer resolves - the audit row deliberately outlives the account.</summary>
    public string UserName { get; set; } = "";

    public DateTime Timestamp { get; set; }

    /// <summary>Empty for Created and Deleted, which carry no field-level diff.</summary>
    public List<AuditChangeDto> Changes { get; set; } = [];
}

/// <summary>One field's before-and-after within an Updated entry.</summary>
public class AuditChangeDto
{
    public string Field { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
}

/// <summary>The filter values actually present in this tenant's trail, for the UI dropdowns.</summary>
public class AuditFilterOptionsDto
{
    public List<string> EntityTypes { get; set; } = [];
    public List<string> Actions { get; set; } = [];
}
