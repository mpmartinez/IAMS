namespace IAMS.Api.Entities;

public class Asset
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string AssetTag { get; set; }
    public string? SerialNumber { get; set; }
    public required string Category { get; set; }
    public required string Status { get; set; }
    public string? Location { get; set; }
    public int? AssignedToUserId { get; set; }
    public User? AssignedToUser { get; set; }
    public decimal? PurchasePrice { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public DateTime? WarrantyExpiry { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public static class AssetStatus
{
    public const string Available = "Available";
    public const string InUse = "InUse";
    public const string Maintenance = "Maintenance";
    public const string Retired = "Retired";
    public const string Lost = "Lost";
}

public static class AssetCategory
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
}
