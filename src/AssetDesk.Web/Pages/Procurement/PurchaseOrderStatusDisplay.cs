namespace AssetDesk.Web.Pages.Procurement;

/// <summary>
/// How a purchase order status is shown - its badge colour and its label. Shared by the list and
/// the detail page so an order reads the same on both.
///
/// Mirrors the five PurchaseOrderStatus values the API defines. They are not referenced directly
/// because the Web project only references AssetDesk.Shared, not the Api project's entities.
/// </summary>
public static class PurchaseOrderStatusDisplay
{
    public static string Variant(string status) => status switch
    {
        "Draft" => "secondary",
        "Ordered" => "info",
        "PartiallyReceived" => "warning",
        "Received" => "success",
        "Cancelled" => "destructive",
        _ => "default"
    };

    /// <summary>The status as a person would write it. The wire value stays PascalCase because
    /// the pages compare against it.</summary>
    public static string Label(string status) => status switch
    {
        "PartiallyReceived" => "Partially Received",
        _ => status
    };
}
