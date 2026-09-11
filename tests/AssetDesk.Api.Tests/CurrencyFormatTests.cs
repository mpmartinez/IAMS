using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The peso sign used to be a constant every call site pasted in. These cover the thing that
/// replaced it, plus the two vocabulary changes that decide which currencies exist at all.
/// </summary>
public class CurrencyFormatTests
{
    [Fact]
    public void Peso_is_the_symbol_for_PHP_and_the_fallback_for_anything_unknown()
    {
        Assert.Equal("₱", CurrencyFormat.SymbolFor(CurrencyFormat.Php));
        Assert.Equal("₱", CurrencyFormat.SymbolFor(null));
        Assert.Equal("₱", CurrencyFormat.SymbolFor("ZZZ"));
    }

    [Fact]
    public void Dollar_is_the_symbol_for_USD()
    {
        Assert.Equal("$", CurrencyFormat.SymbolFor(CurrencyFormat.Usd));
    }

    [Fact]
    public void Amounts_render_with_two_decimals_and_thousands_separators()
    {
        Assert.Equal("₱1,234.50", CurrencyFormat.Format(1234.5m, CurrencyFormat.Php));
        Assert.Equal("$1,200.00", CurrencyFormat.Format(1200m, CurrencyFormat.Usd));
    }

    [Fact]
    public void A_null_amount_renders_as_an_em_dash()
    {
        Assert.Equal("—", CurrencyFormat.Format(null, CurrencyFormat.Php));
    }

    [Fact]
    public void USD_joins_PHP_as_a_supported_currency_and_leaves_the_retired_list()
    {
        Assert.Equal([Currencies.PHP, Currencies.USD], Currencies.All);
        Assert.DoesNotContain(Currencies.USD, Currencies.Retired);
        Assert.Contains(Currencies.EUR, Currencies.Retired);
    }

    [Fact]
    public void The_seeded_USD_lookup_row_is_active_and_the_other_five_are_not()
    {
        var currencyRows = LookupValueSeed.Rows
            .Where(r => r.LookupType == LookupTypes.Currency)
            .ToDictionary(r => r.Value, r => r.IsActive);

        Assert.True(currencyRows[Currencies.PHP]);
        Assert.True(currencyRows[Currencies.USD]);
        Assert.False(currencyRows[Currencies.EUR]);
        Assert.False(currencyRows[Currencies.GBP]);
        Assert.False(currencyRows[Currencies.JPY]);
        Assert.False(currencyRows[Currencies.CAD]);
        Assert.False(currencyRows[Currencies.AUD]);
    }

    [Fact]
    public void The_locked_reason_no_longer_claims_the_app_is_peso_only()
    {
        var reason = LookupTypes.LockedReason(LookupTypes.Currency);

        Assert.NotNull(reason);
        Assert.DoesNotContain("peso-only", reason);
        Assert.Contains("symbol", reason);
    }
}
