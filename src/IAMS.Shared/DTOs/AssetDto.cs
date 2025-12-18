using System.ComponentModel.DataAnnotations;

namespace IAMS.Shared.DTOs;

/// <summary>
/// Warranty status based on days remaining
/// </summary>
public enum WarrantyStatus
{
    /// <summary>No warranty information available</summary>
    None,
    /// <summary>More than 90 days remaining</summary>
    Active,
    /// <summary>90 days or less remaining</summary>
    Expiring,
    /// <summary>Warranty has expired</summary>
    Expired
}

public record AssetDto
{
    public int Id { get; init; }
    public required string AssetTag { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public int? ModelYear { get; init; }
    public string? SerialNumber { get; init; }
    public required string DeviceType { get; init; }
    public decimal? PurchasePrice { get; init; }
    public string Currency { get; init; } = "USD";
    public string? WarrantyProvider { get; init; }
    public DateTime? WarrantyStartDate { get; init; }
    public DateTime? WarrantyEndDate { get; init; }
    public required string Status { get; init; }
    public string? AssignedToUserId { get; init; }
    public string? AssignedToUserName { get; init; }

    // Legacy/additional fields
    public string? Name { get; init; }
    public string? Location { get; init; }
    public DateTime? PurchaseDate { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }

    // Computed
    public string DisplayName => !string.IsNullOrEmpty(Name)
        ? Name
        : $"{Manufacturer ?? "Unknown"} {Model ?? DeviceType}".Trim();

    /// <summary>
    /// Calculated warranty status based on end date
    /// </summary>
    public WarrantyStatus WarrantyStatus => CalculateWarrantyStatus();

    /// <summary>
    /// Days remaining until warranty expires (negative if expired)
    /// </summary>
    public int? WarrantyDaysRemaining => WarrantyEndDate.HasValue
        ? (int)(WarrantyEndDate.Value.Date - DateTime.UtcNow.Date).TotalDays
        : null;

    private WarrantyStatus CalculateWarrantyStatus()
    {
        if (!WarrantyEndDate.HasValue)
            return WarrantyStatus.None;

        var daysRemaining = (WarrantyEndDate.Value.Date - DateTime.UtcNow.Date).TotalDays;

        if (daysRemaining < 0)
            return WarrantyStatus.Expired;
        if (daysRemaining <= 90)
            return WarrantyStatus.Expiring;
        return WarrantyStatus.Active;
    }
}

public record CreateAssetDto
{
    [Required(ErrorMessage = "Device type is required")]
    [StringLength(50, ErrorMessage = "Device type cannot exceed 50 characters")]
    public required string DeviceType { get; init; }

    [StringLength(100, ErrorMessage = "Manufacturer cannot exceed 100 characters")]
    public string? Manufacturer { get; init; }

    [StringLength(100, ErrorMessage = "Model cannot exceed 100 characters")]
    public string? Model { get; init; }

    [Range(1900, 2100, ErrorMessage = "Model year must be between 1900 and 2100")]
    public int? ModelYear { get; init; }

    [StringLength(100, ErrorMessage = "Serial number cannot exceed 100 characters")]
    public string? SerialNumber { get; init; }

    [Range(0, double.MaxValue, ErrorMessage = "Purchase price must be a positive value")]
    public decimal? PurchasePrice { get; init; }

    [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency must be a 3-letter code")]
    public string Currency { get; init; } = "USD";

    [StringLength(200, ErrorMessage = "Warranty provider cannot exceed 200 characters")]
    public string? WarrantyProvider { get; init; }

    public DateTime? WarrantyStartDate { get; init; }
    public DateTime? WarrantyEndDate { get; init; }

    [Required(ErrorMessage = "Status is required")]
    [StringLength(50, ErrorMessage = "Status cannot exceed 50 characters")]
    public required string Status { get; init; }

    public string? AssignedToUserId { get; init; }

    // Legacy/additional fields
    [StringLength(200, ErrorMessage = "Name cannot exceed 200 characters")]
    public string? Name { get; init; }

    [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters")]
    public string? Location { get; init; }

    public DateTime? PurchaseDate { get; init; }

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters")]
    public string? Notes { get; init; }
}

public record UpdateAssetDto
{
    [StringLength(50, ErrorMessage = "Device type cannot exceed 50 characters")]
    public string? DeviceType { get; init; }

    [StringLength(100, ErrorMessage = "Manufacturer cannot exceed 100 characters")]
    public string? Manufacturer { get; init; }

    [StringLength(100, ErrorMessage = "Model cannot exceed 100 characters")]
    public string? Model { get; init; }

    [Range(1900, 2100, ErrorMessage = "Model year must be between 1900 and 2100")]
    public int? ModelYear { get; init; }

    [StringLength(100, ErrorMessage = "Serial number cannot exceed 100 characters")]
    public string? SerialNumber { get; init; }

    [Range(0, double.MaxValue, ErrorMessage = "Purchase price must be a positive value")]
    public decimal? PurchasePrice { get; init; }

    [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency must be a 3-letter code")]
    public string? Currency { get; init; }

    [StringLength(200, ErrorMessage = "Warranty provider cannot exceed 200 characters")]
    public string? WarrantyProvider { get; init; }

    public DateTime? WarrantyStartDate { get; init; }
    public DateTime? WarrantyEndDate { get; init; }

    [StringLength(50, ErrorMessage = "Status cannot exceed 50 characters")]
    public string? Status { get; init; }

    public string? AssignedToUserId { get; init; }

    // Legacy/additional fields
    [StringLength(200, ErrorMessage = "Name cannot exceed 200 characters")]
    public string? Name { get; init; }

    [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters")]
    public string? Location { get; init; }

    public DateTime? PurchaseDate { get; init; }

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters")]
    public string? Notes { get; init; }
}

/// <summary>
/// Alert types for warranty tracking
/// </summary>
public enum WarrantyAlertType
{
    Expiring,
    Expired
}

/// <summary>
/// DTO for warranty alerts
/// </summary>
public record WarrantyAlertDto
{
    public int Id { get; init; }
    public int AssetId { get; init; }
    public required string AssetTag { get; init; }
    public required string AssetDisplayName { get; init; }
    public required string DeviceType { get; init; }
    public WarrantyAlertType AlertType { get; init; }
    public DateTime WarrantyEndDate { get; init; }
    public int DaysRemaining { get; init; }
    public string? WarrantyProvider { get; init; }
    public string? AssignedToUserName { get; init; }
    public string? Location { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? AcknowledgedAt { get; init; }
    public string? AcknowledgedByUserId { get; init; }
    public string? AcknowledgedByUserName { get; init; }
    public bool IsAcknowledged => AcknowledgedAt.HasValue;
}

/// <summary>
/// Summary of warranty alerts
/// </summary>
public record WarrantyAlertSummaryDto
{
    public int TotalAlerts { get; init; }
    public int ExpiringCount { get; init; }
    public int ExpiredCount { get; init; }
    public int UnacknowledgedCount { get; init; }
}

/// <summary>
/// Dashboard statistics
/// </summary>
public record DashboardDto
{
    // Asset counts
    public int TotalAssets { get; init; }
    public int AssignedAssets { get; init; }
    public int UnassignedAssets { get; init; }
    public int AvailableAssets { get; init; }
    public int InUseAssets { get; init; }
    public int MaintenanceAssets { get; init; }

    // Asset value
    public decimal TotalAssetValue { get; init; }
    public string PrimaryCurrency { get; init; } = "USD";

    // Warranty alerts
    public int WarrantiesExpiringSoon { get; init; }
    public int WarrantiesExpired { get; init; }

    // Recent activity
    public List<RecentAssetDto> RecentAssets { get; init; } = new();

    // By device type breakdown
    public List<DeviceTypeCountDto> AssetsByType { get; init; } = new();
}

public record RecentAssetDto
{
    public int Id { get; init; }
    public required string AssetTag { get; init; }
    public required string DisplayName { get; init; }
    public required string DeviceType { get; init; }
    public required string Status { get; init; }
    public string? AssignedToUserName { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record DeviceTypeCountDto
{
    public required string DeviceType { get; init; }
    public int Count { get; init; }
    public decimal TotalValue { get; init; }
}

// Report DTOs

/// <summary>
/// Available report types
/// </summary>
public enum ReportType
{
    AssetInventory,
    AssignedAssetsByUser,
    WarrantyExpiry,
    AssetValue
}

/// <summary>
/// Asset inventory report row
/// </summary>
public record AssetInventoryReportRow
{
    public required string AssetTag { get; init; }
    public required string DeviceType { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public required string Status { get; init; }
    public string? AssignedTo { get; init; }
    public string? Location { get; init; }
    public decimal? PurchasePrice { get; init; }
    public string? Currency { get; init; }
    public DateTime? PurchaseDate { get; init; }
    public DateTime? WarrantyEndDate { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Assigned assets per user report row
/// </summary>
public record AssignedAssetsByUserReportRow
{
    public required string UserName { get; init; }
    public string? Department { get; init; }
    public required string AssetTag { get; init; }
    public required string DeviceType { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public DateTime? AssignedDate { get; init; }
    public decimal? PurchasePrice { get; init; }
    public string? Currency { get; init; }
}

/// <summary>
/// Warranty expiry report row
/// </summary>
public record WarrantyExpiryReportRow
{
    public required string AssetTag { get; init; }
    public required string DeviceType { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? WarrantyProvider { get; init; }
    public DateTime? WarrantyStartDate { get; init; }
    public DateTime WarrantyEndDate { get; init; }
    public int DaysRemaining { get; init; }
    public required string WarrantyStatus { get; init; }
    public string? AssignedTo { get; init; }
    public string? Location { get; init; }
}

/// <summary>
/// Asset value report row
/// </summary>
public record AssetValueReportRow
{
    public required string DeviceType { get; init; }
    public int AssetCount { get; init; }
    public decimal TotalValue { get; init; }
    public decimal AverageValue { get; init; }
    public string Currency { get; init; } = "USD";
}

/// <summary>
/// Asset value summary
/// </summary>
public record AssetValueSummaryDto
{
    public decimal GrandTotalValue { get; init; }
    public int TotalAssetCount { get; init; }
    public decimal AverageAssetValue { get; init; }
    public string PrimaryCurrency { get; init; } = "USD";
    public List<AssetValueReportRow> ByDeviceType { get; init; } = new();
    public List<AssetValueByStatusDto> ByStatus { get; init; } = new();
}

/// <summary>
/// Asset value by status
/// </summary>
public record AssetValueByStatusDto
{
    public required string Status { get; init; }
    public int AssetCount { get; init; }
    public decimal TotalValue { get; init; }
}

/// <summary>
/// Report metadata
/// </summary>
public record ReportMetadataDto
{
    public required string ReportName { get; init; }
    public required string Description { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public required string GeneratedBy { get; init; }
    public int TotalRows { get; init; }
}
