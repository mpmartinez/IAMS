namespace AssetDesk.Api.Entities;

/// <summary>
/// A piece of software the organisation is entitled to use. Deactivated, never deleted: its
/// entitlements are the record of what was bought and its seats the record of who had it.
/// </summary>
public class SoftwareLicence : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public required string Name { get; set; }
    public string? Publisher { get; set; }

    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>LicenceModels.PerUser or PerDevice. Fixed once any seat exists.</summary>
    public required string LicenceModel { get; set; }

    /// <summary>
    /// Never returned in full except by the audited reveal endpoint, and redacted in the automatic
    /// change log - AuditSaveChangesInterceptor would otherwise write it into a table that only grows.
    /// </summary>
    public string? LicenceKey { get; set; }

    /// <summary>Null means perpetual.</summary>
    public DateTime? ExpiresAt { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<LicenceEntitlement> Entitlements { get; set; } = [];
    public ICollection<LicenceSeatAssignment> Seats { get; set; } = [];
}

/// <summary>
/// One acquisition of seats, or a renewal of them. Seats owned is the sum of SeatsAdded rather than
/// a column on the licence, for the reason GoodsReceipt carries its own rate: fifty seats bought in
/// January and ten in June cost different pesos, and a single count cannot say so.
/// </summary>
public class LicenceEntitlement : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int SoftwareLicenceId { get; set; }
    public SoftwareLicence? SoftwareLicence { get; set; }

    /// <summary>Positive for a purchase, zero for a renewal, negative for a recorded reduction.</summary>
    public int SeatsAdded { get; set; }

    /// <summary>The total for this entry, in Currency.</summary>
    public decimal Cost { get; set; }

    public string Currency { get; set; } = Currencies.PHP;

    /// <summary>Pesos per one unit of Currency, as booked. Exactly 1 for PHP.</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    public DateTime EntitlementDate { get; set; } = DateTime.UtcNow;

    /// <summary>Set when this entry renewed the licence, or dated it for the first time.</summary>
    public DateTime? ExpiresAtAfter { get; set; }

    /// <summary>
    /// The receipt line this entry came from, when it came from receiving a purchase order. Null
    /// for an entry recorded by hand - the same honesty Asset.GoodsReceiptLineId keeps.
    /// </summary>
    public int? GoodsReceiptLineId { get; set; }
    public GoodsReceiptLine? GoodsReceiptLine { get; set; }

    public required string CreatedByUserId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A seat given to a person or to a device. Active while ReleasedAt is null; a released seat stays
/// as history. Exactly one of UserId and AssetId is set - enforced by a check constraint, so no code
/// path can write a seat that belongs to nobody or to both.
/// </summary>
public class LicenceSeatAssignment : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int SoftwareLicenceId { get; set; }
    public SoftwareLicence? SoftwareLicence { get; set; }

    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAt { get; set; }

    public required string AssignedByUserId { get; set; }
    public string? ReleasedByUserId { get; set; }
    public string? Notes { get; set; }

    public bool IsActive => ReleasedAt is null;
}
