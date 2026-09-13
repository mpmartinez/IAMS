namespace AssetDesk.Shared;

/// <summary>
/// How a licence is counted. Shared rather than kept in the Api's entities because the Web
/// project branches on it too - the seat picker offers people for one and devices for the other.
/// </summary>
public static class LicenceModels
{
    public const string PerUser = "PerUser";
    public const string PerDevice = "PerDevice";

    public static readonly string[] All = [PerUser, PerDevice];

    public static bool IsValid(string? model) => model is not null && All.Contains(model);

    public static string Label(string model) => model switch
    {
        PerUser => "Per user",
        PerDevice => "Per device",
        _ => model
    };
}

/// <summary>Computed on read from a licence's expiry - never stored, so it cannot go stale.</summary>
public static class LicenceRenewalStatuses
{
    public const string Perpetual = "Perpetual";
    public const string Active = "Active";
    public const string Due = "Due";
    public const string Expired = "Expired";
}

/// <summary>
/// What receiving a Software purchase-order line does to its licence. A renewal records the cost
/// and moves the expiry without adding seats: receiving "50 seats, 2027 renewal" against a licence
/// that already owns 50 must not report 100 owned.
/// </summary>
public static class LicenceReceiptModes
{
    public const string AddSeats = "AddSeats";
    public const string Renew = "Renew";

    public static bool IsValid(string? mode) => mode is AddSeats or Renew;
}
