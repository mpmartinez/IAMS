using AssetDesk.Shared;

namespace AssetDesk.Api.Entities;

/// <summary>
/// The rules that turn stored licence facts into what a person needs to see. Pure - no DbContext,
/// no clock - so every caller passes "today" and every rule is testable at its edges.
/// </summary>
public static class LicenceRules
{
    /// <summary>The same window WarrantyCheckService uses for an expiring warranty.</summary>
    public const int RenewalWindowDays = 90;

    public static int? DaysUntilExpiry(DateTime? expiresAt, DateTime today) =>
        expiresAt is { } expiry ? (expiry.Date - today.Date).Days : null;

    public static string RenewalStatus(DateTime? expiresAt, DateTime today) =>
        DaysUntilExpiry(expiresAt, today) switch
        {
            null => LicenceRenewalStatuses.Perpetual,
            < 0 => LicenceRenewalStatuses.Expired,
            <= RenewalWindowDays => LicenceRenewalStatuses.Due,
            _ => LicenceRenewalStatuses.Active
        };

    public static bool NeedsRenewalAttention(string status) =>
        status is LicenceRenewalStatuses.Due or LicenceRenewalStatuses.Expired;

    /// <summary>
    /// A key shorter than eight characters is masked entirely: the last four of a five-character
    /// key is most of the key.
    /// </summary>
    public static string? Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return key.Length < 8 ? "****" : $"****-{key[^4..]}";
    }

    /// <summary>
    /// A seat still held by someone who has left, or by a machine that is retired or lost. It is
    /// still counted as assigned - it is, until someone releases it - but it is where the saving is.
    /// </summary>
    public static bool IsReclaimable(bool? userIsActive, string? assetStatus) =>
        userIsActive == false || assetStatus is AssetStatus.Retired or AssetStatus.Lost;
}
