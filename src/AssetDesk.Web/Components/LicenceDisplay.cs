using AssetDesk.Shared;

namespace AssetDesk.Web.Components;

/// <summary>
/// How a licence's renewal status is shown. Shared by the licence list, the licence page and the
/// asset page so an expiring licence reads the same everywhere.
/// </summary>
public static class LicenceDisplay
{
    public static string RenewalVariant(string status) => status switch
    {
        LicenceRenewalStatuses.Expired => "destructive",
        LicenceRenewalStatuses.Due => "warning",
        LicenceRenewalStatuses.Active => "success",
        _ => "secondary"
    };

    public static string RenewalLabel(string status, int? daysUntilExpiry) => status switch
    {
        LicenceRenewalStatuses.Expired when daysUntilExpiry is { } days =>
            -days == 1 ? "Expired yesterday" : $"Expired {-days} days ago",
        LicenceRenewalStatuses.Expired => "Expired",
        LicenceRenewalStatuses.Due => daysUntilExpiry switch
        {
            0 => "Renews today",
            1 => "Renews tomorrow",
            { } days => $"Renews in {days} days",
            _ => "Renewal due"
        },
        LicenceRenewalStatuses.Active => "Active",
        _ => "Perpetual"
    };
}
