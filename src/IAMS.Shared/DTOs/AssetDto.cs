namespace IAMS.Shared.DTOs;

public record AssetDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string AssetTag { get; init; }
    public string? SerialNumber { get; init; }
    public required string Category { get; init; }
    public required string Status { get; init; }
    public string? Location { get; init; }
    public string? AssignedToUserId { get; init; }
    public string? AssignedToUserName { get; init; }
    public decimal? PurchasePrice { get; init; }
    public DateTime? PurchaseDate { get; init; }
    public DateTime? WarrantyExpiry { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public record CreateAssetDto
{
    public required string Name { get; init; }
    public required string AssetTag { get; init; }
    public string? SerialNumber { get; init; }
    public required string Category { get; init; }
    public required string Status { get; init; }
    public string? Location { get; init; }
    public string? AssignedToUserId { get; init; }
    public decimal? PurchasePrice { get; init; }
    public DateTime? PurchaseDate { get; init; }
    public DateTime? WarrantyExpiry { get; init; }
    public string? Notes { get; init; }
}

public record UpdateAssetDto
{
    public string? Name { get; init; }
    public string? AssetTag { get; init; }
    public string? SerialNumber { get; init; }
    public string? Category { get; init; }
    public string? Status { get; init; }
    public string? Location { get; init; }
    public string? AssignedToUserId { get; init; }
    public decimal? PurchasePrice { get; init; }
    public DateTime? PurchaseDate { get; init; }
    public DateTime? WarrantyExpiry { get; init; }
    public string? Notes { get; init; }
}
