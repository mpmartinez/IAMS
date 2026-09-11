using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// A currency without a sane rate is how the figures get quietly wrong: a rate of zero
/// removes the asset from every total, and "PHP at 58.20" multiplies a peso figure by 58.
/// </summary>
public class AssetCurrencyValidationTests
{
    private static CreateAssetDto NewAsset(string currency, decimal rate) => new()
    {
        Name = "Test Laptop",
        DeviceType = DeviceTypes.Laptop,
        Status = AssetStatus.Available,
        PurchasePrice = 1200m,
        Currency = currency,
        ExchangeRate = rate
    };

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_rate_of_zero_or_less_is_rejected(decimal rate)
    {
        var error = CurrencyRules.Validate(Currencies.USD, rate);
        Assert.NotNull(error);
        Assert.Contains("greater than zero", error);
    }

    [Fact]
    public void PHP_must_be_booked_at_exactly_one()
    {
        var error = CurrencyRules.Validate(Currencies.PHP, 58.20m);
        Assert.NotNull(error);
        Assert.Contains("PHP", error);
        Assert.Contains("1", error);
    }

    [Fact]
    public void PHP_at_one_is_accepted()
    {
        Assert.Null(CurrencyRules.Validate(Currencies.PHP, 1m));
    }

    [Fact]
    public void USD_at_a_real_rate_is_accepted()
    {
        Assert.Null(CurrencyRules.Validate(Currencies.USD, 58.20m));
    }

    [Fact]
    public void USD_left_at_the_default_rate_of_one_is_rejected()
    {
        var error = CurrencyRules.Validate(Currencies.USD, 1m);
        Assert.NotNull(error);
        Assert.Contains("rate is required", error);
    }
}
