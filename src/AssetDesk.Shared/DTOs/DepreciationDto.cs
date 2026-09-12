using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

public record DepreciationPolicyDto
{
    public int Id { get; init; }
    public required string DeviceType { get; init; }
    public int UsefulLifeMonths { get; init; }
    public decimal ResidualPercent { get; init; }
}

public record UpsertDepreciationPolicyDto
{
    [Required]
    public required string DeviceType { get; init; }

    [Range(1, 1200, ErrorMessage = "Useful life must be between 1 and 1200 months")]
    public int UsefulLifeMonths { get; init; }

    [Range(0, 100, ErrorMessage = "Residual must be between 0 and 100 percent")]
    public decimal ResidualPercent { get; init; }
}
