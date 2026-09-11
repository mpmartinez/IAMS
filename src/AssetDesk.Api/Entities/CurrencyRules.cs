namespace AssetDesk.Api.Entities;

/// <summary>
/// The rules binding a currency to its exchange rate. One home for all three, because both
/// AssetsController and AssetImportService enforce them and a copy in each would drift.
///
/// Deliberately here rather than on the controller: AssetImportService is a service, and a
/// service reaching into a controller inverts the dependency direction the rest of the
/// codebase follows.
/// </summary>
public static class CurrencyRules
{
    /// <summary>
    /// Returns null when the pair is valid, otherwise the message to show. The wording is kept
    /// neutral so it reads correctly whether it lands in an API response or against a
    /// spreadsheet row.
    /// </summary>
    public static string? Validate(string currency, decimal rate)
    {
        if (rate <= 0)
            return "Exchange rate must be greater than zero.";

        if (currency == Currencies.PHP && rate != 1m)
            return $"{Currencies.PHP} is the reporting currency, so its exchange rate must be exactly 1.";

        if (currency != Currencies.PHP && rate == 1m)
            return $"An exchange rate is required for {currency} - pesos per 1 {currency}.";

        return null;
    }
}
