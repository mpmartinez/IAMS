namespace AssetDesk.Shared;

/// <summary>
/// How an amount is rendered, for both the API (PDF and CSV output) and the Blazor client.
/// Lives in Shared rather than beside <c>Currencies</c> in the API project because
/// AssetDesk.Web references only this assembly - which is why Reports.razor used to paste in
/// its own peso sign.
///
/// A currency is only supported once it has a symbol here, which is the reason the Currency
/// lookup type stays locked: an admin activating a row for a code this switch has never heard
/// of would render every amount for it with a peso sign.
/// </summary>
public static class CurrencyFormat
{
    public const string Php = "PHP";
    public const string Usd = "USD";

    /// <summary>Falls back to the peso sign: every row predating multi-currency is a peso row.</summary>
    public static string SymbolFor(string? code) => code switch
    {
        Usd => "$",
        _ => "₱"
    };

    /// <summary>Renders an amount, or an em dash when there is no amount to render.</summary>
    public static string Format(decimal? amount, string? code) =>
        amount.HasValue ? $"{SymbolFor(code)}{amount.Value:N2}" : "—";
}
