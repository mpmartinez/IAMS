namespace AssetDesk.Api.Entities;

public class Asset : ITenantEntity
{
    public int Id { get; set; }

    // Multi-tenancy
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public required string AssetTag { get; set; } // Auto-generated
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public int? ModelYear { get; set; }
    public string? SerialNumber { get; set; }
    public required string DeviceType { get; set; }
    public decimal? PurchasePrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string? WarrantyProvider { get; set; }
    public DateTime? WarrantyStartDate { get; set; }
    public DateTime? WarrantyEndDate { get; set; }
    public required string Status { get; set; }
    public string? AssignedToUserId { get; set; }
    public ApplicationUser? AssignedToUser { get; set; }

    /// <summary>The person accountable for this asset, as distinct from whoever currently holds it.</summary>
    public string? OwnerUserId { get; set; }
    public ApplicationUser? OwnerUser { get; set; }

    /// <summary>Stamped by a QR verification scan. Drives inventory-accuracy reporting.</summary>
    public DateTime? LastVerifiedAt { get; set; }

    // Legacy/additional fields
    public string? Name { get; set; }
    public string? Location { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Computed display name
    public string DisplayName => !string.IsNullOrEmpty(Name)
        ? Name
        : $"{Manufacturer ?? "Unknown"} {Model ?? DeviceType}".Trim();
}

public static class AssetStatus
{
    public const string Available = "Available";
    public const string InUse = "InUse";
    public const string Maintenance = "Maintenance";
    public const string Retired = "Retired";
    public const string Lost = "Lost";

    // Locked lookup type (see LookupTypes) - code branches on these exact values, so this
    // list exists for display/seeding purposes only. Validation sites keep their own literal
    // array rather than reading this, matching how they validated before lookups existed.
    public static readonly string[] All = [Available, InUse, Maintenance, Retired, Lost];
}

public static class DeviceTypes
{
    public const string Laptop = "Laptop";
    public const string Desktop = "Desktop";
    public const string Monitor = "Monitor";
    public const string Phone = "Phone";
    public const string Tablet = "Tablet";
    public const string Printer = "Printer";
    public const string Network = "Network";
    public const string Server = "Server";
    public const string Peripheral = "Peripheral";
    public const string Software = "Software";
    public const string Other = "Other";

    public static readonly string[] All = [Laptop, Desktop, Monitor, Phone, Tablet, Printer, Network, Server, Peripheral, Software, Other];
}

public static class Currencies
{
    public const string USD = "USD";
    public const string EUR = "EUR";
    public const string GBP = "GBP";
    public const string PHP = "PHP";
    public const string JPY = "JPY";
    public const string CAD = "CAD";
    public const string AUD = "AUD";

    public static readonly string[] All = [USD, EUR, GBP, PHP, JPY, CAD, AUD];
}
