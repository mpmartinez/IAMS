namespace AssetDesk.Api.Entities;

/// <summary>
/// Someone the organisation buys from. Deactivated rather than deleted once purchase orders
/// reference it - the same reasoning LookupValue documents for its own rows.
/// </summary>
public class Supplier : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public required string Name { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
