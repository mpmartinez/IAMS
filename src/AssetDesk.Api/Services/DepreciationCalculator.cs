using AssetDesk.Api.Entities;

namespace AssetDesk.Api.Services;

/// <summary>Why an asset has no book value. Grouped and counted by the depreciation report.</summary>
public static class NotDepreciableReasons
{
    public const string NoPurchasePrice = "No purchase price";
    public const string NoPurchaseDate = "No purchase date";
    public const string NoPolicy = "No depreciation policy for this device type";
    public const string InvalidUsefulLife = "Depreciation policy has a useful life of zero or less";
    public const string InvalidResidual = "Depreciation policy has a residual outside 0 to 100 percent";
}

/// <param name="IsDepreciable">False when <paramref name="NotDepreciableReason"/> explains why not.</param>
/// <param name="CostBasis">Peso cost - purchase price times exchange rate. Populated even when not depreciable.</param>
public readonly record struct DepreciationResult(
    bool IsDepreciable,
    string? NotDepreciableReason,
    decimal CostBasis,
    decimal AccumulatedDepreciation,
    decimal NetBookValue,
    int ElapsedMonths,
    bool IsFullyDepreciated);

/// <summary>
/// Straight-line depreciation, full-month convention. Deliberately pure: no DbContext and no
/// DateTime.UtcNow - the as-of date is a parameter, which is what lets every case be pinned by
/// arithmetic alone.
///
/// Rounding is left to the caller. Rounding here would compound over a 36-month life into a
/// visible discrepancy between accumulated depreciation and cost.
/// </summary>
public static class DepreciationCalculator
{
    public static DepreciationResult Calculate(
        decimal? purchasePrice,
        decimal exchangeRate,
        DateTime? purchaseDate,
        DepreciationPolicy? policy,
        DateTime asOf)
    {
        var costBasis = (purchasePrice ?? 0m) * exchangeRate;

        if (purchasePrice is null)
            return NotDepreciable(NotDepreciableReasons.NoPurchasePrice, costBasis);

        if (purchaseDate is null)
            return NotDepreciable(NotDepreciableReasons.NoPurchaseDate, costBasis);

        if (policy is null)
            return NotDepreciable(NotDepreciableReasons.NoPolicy, costBasis);

        // Second line of defence: the policy screen rejects this first. A pure function can be
        // called by anything, and a DivideByZeroException surfacing inside a report is a worse
        // failure than a row reported as undepreciable.
        if (policy.UsefulLifeMonths <= 0)
            return NotDepreciable(NotDepreciableReasons.InvalidUsefulLife, costBasis);

        // Second line of defence, same reasoning as the useful-life guard above: the policy
        // screen is meant to keep this in 0-100, but nothing in the database enforces that
        // range today, and a pure function can be called by anything.
        if (policy.ResidualPercent < 0m || policy.ResidualPercent > 100m)
            return NotDepreciable(NotDepreciableReasons.InvalidResidual, costBasis);

        var residual = costBasis * policy.ResidualPercent / 100m;
        var depreciable = costBasis - residual;
        var monthly = depreciable / policy.UsefulLifeMonths;

        // The + 1 is the full-month convention: the purchase month is month one, so an asset
        // bought on any day in March depreciates for the whole of March. Clamped at zero because
        // nothing stops an admin typing a future purchase date, and a negative elapsed count
        // would put book value above cost.
        var elapsed = Math.Max(0,
            (asOf.Year - purchaseDate.Value.Year) * 12
            + (asOf.Month - purchaseDate.Value.Month)
            + 1);

        var accumulated = Math.Min(monthly * elapsed, depreciable);

        return new DepreciationResult(
            IsDepreciable: true,
            NotDepreciableReason: null,
            CostBasis: costBasis,
            AccumulatedDepreciation: accumulated,
            NetBookValue: costBasis - accumulated,
            ElapsedMonths: elapsed,
            IsFullyDepreciated: elapsed >= policy.UsefulLifeMonths);
    }

    private static DepreciationResult NotDepreciable(string reason, decimal costBasis) =>
        new(IsDepreciable: false,
            NotDepreciableReason: reason,
            CostBasis: costBasis,
            AccumulatedDepreciation: 0m,
            NetBookValue: 0m,
            ElapsedMonths: 0,
            IsFullyDepreciated: false);
}
