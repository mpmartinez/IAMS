using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The calculator is a pure function, so it can be pinned exhaustively here and the report in
/// Task 5 becomes a mapping exercise. Every case below is arithmetic with no database.
/// </summary>
public class DepreciationCalculatorTests
{
    private static DepreciationPolicy Policy(int months, decimal residualPercent) => new()
    {
        DeviceType = DeviceTypes.Laptop,
        UsefulLifeMonths = months,
        ResidualPercent = residualPercent
    };

    // 36 months, 10% residual, cost 60,000 -> residual 6,000, depreciable 54,000, 1,500/month.
    private static DepreciationResult Standard(DateTime purchase, DateTime asOf) =>
        DepreciationCalculator.Calculate(60000m, 1m, purchase, Policy(36, 10m), asOf);

    [Fact]
    public void The_purchase_month_counts_as_month_one()
    {
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2026, 3, 31));

        Assert.True(r.IsDepreciable);
        Assert.Equal(1, r.ElapsedMonths);
        Assert.Equal(1500m, r.AccumulatedDepreciation);
        Assert.Equal(58500m, r.NetBookValue);
        Assert.False(r.IsFullyDepreciated);
    }

    [Fact]
    public void The_day_of_the_month_does_not_matter()
    {
        var first = Standard(new DateTime(2026, 3, 1), new DateTime(2026, 3, 2));
        var last = Standard(new DateTime(2026, 3, 31), new DateTime(2026, 3, 31));

        Assert.Equal(first.ElapsedMonths, last.ElapsedMonths);
        Assert.Equal(1500m, first.AccumulatedDepreciation);
        Assert.Equal(first.NetBookValue, last.NetBookValue);
    }

    [Fact]
    public void Mid_life_accumulates_one_month_at_a_time()
    {
        // March 2026 is month 1, so March 2027 is month 13.
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2027, 3, 5));

        Assert.Equal(13, r.ElapsedMonths);
        Assert.Equal(19500m, r.AccumulatedDepreciation);
        Assert.Equal(40500m, r.NetBookValue);
    }

    [Fact]
    public void The_final_month_lands_exactly_on_the_residual()
    {
        // Month 36 of a 36-month life bought March 2026 is February 2029.
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2029, 2, 28));

        Assert.Equal(36, r.ElapsedMonths);
        Assert.Equal(54000m, r.AccumulatedDepreciation);
        Assert.Equal(6000m, r.NetBookValue);
        Assert.True(r.IsFullyDepreciated);
    }

    [Fact]
    public void Past_end_of_life_the_book_value_holds_at_the_residual()
    {
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2035, 1, 1));

        Assert.Equal(54000m, r.AccumulatedDepreciation);
        Assert.Equal(6000m, r.NetBookValue);
        Assert.True(r.IsFullyDepreciated);
    }

    [Fact]
    public void With_no_residual_the_asset_reaches_exactly_zero()
    {
        var r = DepreciationCalculator.Calculate(
            36000m, 1m, new DateTime(2026, 1, 10), Policy(36, 0m), new DateTime(2028, 12, 31));

        Assert.Equal(36000m, r.AccumulatedDepreciation);
        Assert.Equal(0m, r.NetBookValue);
        Assert.True(r.IsFullyDepreciated);
    }

    [Fact]
    public void A_future_purchase_date_clamps_to_zero_rather_than_exceeding_cost()
    {
        var r = Standard(new DateTime(2027, 6, 1), new DateTime(2026, 3, 20));

        Assert.Equal(0, r.ElapsedMonths);
        Assert.Equal(0m, r.AccumulatedDepreciation);
        Assert.Equal(60000m, r.NetBookValue);
        Assert.False(r.IsFullyDepreciated);
    }

    [Fact]
    public void Cost_basis_goes_through_the_exchange_rate()
    {
        // USD 1,200 at 58.20 = 69,840 pesos. 24 months, no residual -> 2,910/month.
        var r = DepreciationCalculator.Calculate(
            1200m, 58.20m, new DateTime(2026, 1, 5), Policy(24, 0m), new DateTime(2026, 2, 1));

        Assert.Equal(69840m, r.CostBasis);
        Assert.Equal(2, r.ElapsedMonths);
        Assert.Equal(5820m, r.AccumulatedDepreciation);
        Assert.Equal(64020m, r.NetBookValue);
    }

    [Fact]
    public void No_purchase_price_is_not_depreciable()
    {
        var r = DepreciationCalculator.Calculate(
            null, 1m, new DateTime(2026, 3, 1), Policy(36, 10m), new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.NoPurchasePrice, r.NotDepreciableReason);
        Assert.Equal(0m, r.NetBookValue);
    }

    [Fact]
    public void No_purchase_date_is_not_depreciable()
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, null, Policy(36, 10m), new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.NoPurchaseDate, r.NotDepreciableReason);
    }

    [Fact]
    public void No_policy_is_not_depreciable()
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), null, new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.NoPolicy, r.NotDepreciableReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void A_non_positive_useful_life_is_reported_not_divided_by(int months)
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), Policy(months, 10m), new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.InvalidUsefulLife, r.NotDepreciableReason);
    }

    [Fact]
    public void A_not_depreciable_asset_still_reports_its_cost_basis()
    {
        // The report totals cost basis across every asset, depreciable or not, so it must be
        // present even when book value cannot be computed.
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), null, new DateTime(2026, 6, 1));

        Assert.Equal(60000m, r.CostBasis);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void A_residual_outside_zero_to_a_hundred_is_reported_not_applied(decimal residualPercent)
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), Policy(36, residualPercent), new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.InvalidResidual, r.NotDepreciableReason);
    }

    [Fact]
    public void A_hundred_percent_residual_is_the_valid_boundary_and_never_depreciates_below_cost()
    {
        // 100% residual: depreciable amount is zero, so book value should hold at cost.
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), Policy(36, 100m), new DateTime(2029, 2, 28));

        Assert.True(r.IsDepreciable);
        Assert.Equal(0m, r.AccumulatedDepreciation);
        Assert.Equal(60000m, r.NetBookValue);
    }

    [Fact]
    public void A_zero_percent_residual_is_the_valid_boundary_and_depreciates_to_zero()
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), Policy(36, 0m), new DateTime(2029, 2, 28));

        Assert.True(r.IsDepreciable);
        Assert.Equal(60000m, r.AccumulatedDepreciation);
        Assert.Equal(0m, r.NetBookValue);
    }

    [Fact]
    public void Intermediate_values_are_not_rounded()
    {
        // 10,000 over 3 months, no residual: monthly is 3333.333... which does not divide
        // evenly. If the calculator rounded that intermediate to 2dp, the sum over 3 months
        // would land at 9999.99, not 10,000.
        var r = DepreciationCalculator.Calculate(
            10000m, 1m, new DateTime(2026, 1, 10), Policy(3, 0m), new DateTime(2026, 3, 15));

        Assert.Equal(3, r.ElapsedMonths);
        Assert.Equal(10000m, r.AccumulatedDepreciation);
        Assert.Equal(0m, r.NetBookValue);
    }

    [Theory]
    // A negative purchase price, and a negative exchange rate, each give a negative cost basis.
    [InlineData(-60000, 1)]
    [InlineData(60000, -1)]
    public void A_negative_cost_basis_is_not_depreciable(decimal price, decimal rate)
    {
        // Unreachable through the app today - the DTO's [Range], CurrencyRules.Validate and the
        // importer all refuse it - but the calculator's other guards exist on exactly the same
        // reasoning: a pure function can be called by anything. Without this guard `depreciable`
        // goes negative and Math.Min picks the *more* negative operand, so accumulated
        // depreciation exceeds cost and book value reads negative from month one.
        var r = DepreciationCalculator.Calculate(
            price, rate, new DateTime(2026, 1, 10), Policy(36, 10m), new DateTime(2026, 2, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.NegativeCostBasis, r.NotDepreciableReason);

        // The not-depreciable contract: the report maps these to null, never to a figure.
        Assert.Equal(0m, r.AccumulatedDepreciation);
        Assert.Equal(0m, r.NetBookValue);
        Assert.Equal(0, r.ElapsedMonths);
    }

    [Fact]
    public void A_zero_cost_basis_is_still_depreciable()
    {
        // The guard is on negative, not on "not positive". A free asset is a real thing to
        // record and depreciates correctly to zero.
        var r = DepreciationCalculator.Calculate(
            0m, 1m, new DateTime(2026, 1, 10), Policy(36, 10m), new DateTime(2026, 2, 1));

        Assert.True(r.IsDepreciable);
        Assert.Equal(0m, r.NetBookValue);
    }
}