namespace AssetDesk.Api.Entities;

/// <summary>
/// How one device type depreciates, for one organisation. Straight-line is the only method, so
/// there is deliberately no Method column - see the plan's global constraints for why.
///
/// Tenant-scoped, and it has to be: LookupValue carries no TenantId because the owner wants one
/// shared vocabulary across every tenant, but useful life is each organisation's own accounting
/// policy rather than vocabulary.
/// </summary>
public class DepreciationPolicy : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Matches <see cref="Asset.DeviceType"/>. Not a foreign key: a device type is a lookup
    /// value stored as a raw string on the asset row, exactly as Asset.DeviceType stores it.
    /// </summary>
    public required string DeviceType { get; set; }

    /// <summary>Months, not years - the arithmetic is monthly and this avoids fractional years.</summary>
    public int UsefulLifeMonths { get; set; }

    /// <summary>
    /// Percent of cost basis retained at end of life, 0-100. A percentage rather than an amount
    /// because one policy row covers assets of very different cost.
    /// </summary>
    public decimal ResidualPercent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
