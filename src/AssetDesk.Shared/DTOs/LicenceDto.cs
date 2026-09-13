using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

public record SoftwareLicenceDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public int? SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public required string LicenceModel { get; init; }

    /// <summary>
    /// The only form of the key any read carries - e.g. "****-X7Q2", or null when there is none.
    /// The full key comes only from the audited reveal endpoint.
    /// </summary>
    public string? MaskedKey { get; init; }

    public DateTime? ExpiresAt { get; init; }
    public required string RenewalStatus { get; init; }
    public int? DaysUntilExpiry { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }

    public int SeatsOwned { get; init; }
    public int SeatsAssigned { get; init; }

    /// <summary>How far assignments exceed what is owned. Allowed and recorded, never blocked.</summary>
    public int OverAssigned { get; init; }

    /// <summary>Active seats held by a deactivated person or a retired or lost device.</summary>
    public int Reclaimable { get; init; }

    /// <summary>True once any seat exists; the licence model cannot change after that.</summary>
    public bool ModelLocked { get; init; }
}

public record SoftwareLicenceDetailDto
{
    public required SoftwareLicenceDto Licence { get; init; }
    public decimal SpendInPesos { get; init; }

    /// <summary>Newest first.</summary>
    public List<LicenceEntitlementDto> Entitlements { get; init; } = [];

    /// <summary>Active seats first, then released ones, each group newest first.</summary>
    public List<LicenceSeatDto> Seats { get; init; } = [];
}

public record LicenceEntitlementDto
{
    public int Id { get; init; }
    public int SeatsAdded { get; init; }
    public decimal Cost { get; init; }
    public required string Currency { get; init; }
    public decimal ExchangeRate { get; init; }
    public decimal CostInPesos { get; init; }
    public DateTime EntitlementDate { get; init; }
    public DateTime? ExpiresAtAfter { get; init; }
    public int? PurchaseOrderId { get; init; }
    public string? PurchaseOrderReference { get; init; }
    public string? CreatedByName { get; init; }
    public string? Notes { get; init; }
}

public record LicenceSeatDto
{
    public int Id { get; init; }
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public int? AssetId { get; init; }
    public string? AssetTag { get; init; }
    public string? AssetName { get; init; }
    public string? AssetStatus { get; init; }
    public DateTime AssignedAt { get; init; }
    public DateTime? ReleasedAt { get; init; }
    public string? AssignedByName { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public bool IsReclaimable { get; init; }
}

public record UpsertSoftwareLicenceDto
{
    [Required, StringLength(200)]
    public required string Name { get; init; }

    [StringLength(200)]
    public string? Publisher { get; init; }

    public int? SupplierId { get; init; }

    [Required]
    public string LicenceModel { get; init; } = "PerUser";

    /// <summary>
    /// Null or blank keeps the stored key - the form never receives the key, so it cannot send it
    /// back. A value replaces it; ClearKey removes it.
    /// </summary>
    [StringLength(500)]
    public string? LicenceKey { get; init; }

    public bool ClearKey { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
}

public record RevealedLicenceKeyDto
{
    public required string LicenceKey { get; init; }
}

public record AddLicenceEntitlementDto
{
    /// <summary>Positive adds seats, zero renews, negative records a reduction.</summary>
    [Range(-100000, 100000)]
    public int SeatsAdded { get; init; }

    [Range(0, 1000000000, ErrorMessage = "A cost cannot be negative")]
    public decimal Cost { get; init; }

    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = "PHP";

    [Range(0.000001, 1000000, ErrorMessage = "Exchange rate must be greater than zero")]
    public decimal ExchangeRate { get; init; } = 1m;

    public DateTime EntitlementDate { get; init; } = DateTime.UtcNow;

    /// <summary>The licence's new expiry, when this entry renews it or dates it for the first time.</summary>
    public DateTime? ExpiresAt { get; init; }

    public string? Notes { get; init; }
}
