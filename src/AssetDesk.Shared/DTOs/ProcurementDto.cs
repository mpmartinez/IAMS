using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

public record SupplierDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? ContactName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
}

public record UpsertSupplierDto
{
    [Required, StringLength(200)]
    public required string Name { get; init; }

    [StringLength(200)] public string? ContactName { get; init; }
    [EmailAddress, StringLength(256)] public string? Email { get; init; }
    [StringLength(50)] public string? Phone { get; init; }
    [StringLength(500)] public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
}
