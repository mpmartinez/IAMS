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

public record DepreciationReportRow
{
    public required string AssetTag { get; init; }
    public required string DeviceType { get; init; }
    public string? Name { get; init; }
    public DateTime? PurchaseDate { get; init; }

    /// <summary>Peso cost - purchase price times exchange rate. Present even when not depreciable.</summary>
    public decimal CostBasis { get; init; }

    /// <summary>The currency the asset was invoiced in, so the row can show what was actually paid.</summary>
    public string? Currency { get; init; }
    public decimal? PurchasePrice { get; init; }

    public int? UsefulLifeMonths { get; init; }
    public int? ElapsedMonths { get; init; }
    public decimal? AccumulatedDepreciation { get; init; }
    public decimal? NetBookValue { get; init; }
    public bool IsDepreciable { get; init; }
    public bool IsFullyDepreciated { get; init; }
    public string? NotDepreciableReason { get; init; }
}

public record NotDepreciableReasonCount
{
    public required string Reason { get; init; }
    public int Count { get; init; }
}

public record DepreciationSummaryDto
{
    public decimal TotalCostBasis { get; init; }
    public decimal TotalAccumulatedDepreciation { get; init; }
    public decimal TotalNetBookValue { get; init; }
    public int DepreciableCount { get; init; }
    public int NotDepreciableCount { get; init; }
    public List<NotDepreciableReasonCount> NotDepreciableByReason { get; init; } = [];

    /// <summary>Always PHP. Every total this system produces is in pesos.</summary>
    public string PrimaryCurrency { get; init; } = "PHP";

    public DateTime AsOf { get; init; }
    public List<DepreciationReportRow> Rows { get; init; } = [];
}
