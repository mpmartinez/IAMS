using Microsoft.AspNetCore.Components;

namespace AssetDesk.Web.Components.Dashboard;

/// <summary>
/// How a device type is drawn - the tint behind its icon, its progress-bar colour, and the icon
/// itself. Shared by <c>EstateDashboard</c> and <c>MyDashboard</c> so the same laptop looks the
/// same on both, rather than the SVG table being pasted into each.
///
/// Device type strings come from <c>AssetDesk.Shared</c>'s DeviceTypes; anything unrecognised
/// falls through to the neutral slate treatment rather than rendering nothing.
/// </summary>
public static class DeviceTypeVisuals
{
    public static string GetBackgroundClass(string deviceType) => deviceType switch
    {
        "Laptop" or "Desktop" => "bg-blue-100 dark:bg-blue-900/30",
        "Monitor" => "bg-purple-100 dark:bg-purple-900/30",
        "Phone" or "Tablet" => "bg-green-100 dark:bg-green-900/30",
        "Printer" => "bg-orange-100 dark:bg-orange-900/30",
        "Network" or "Server" => "bg-red-100 dark:bg-red-900/30",
        _ => "bg-slate-100 dark:bg-slate-700"
    };

    public static string GetBarColorClass(string deviceType) => deviceType switch
    {
        "Laptop" or "Desktop" => "bg-blue-500",
        "Monitor" => "bg-purple-500",
        "Phone" or "Tablet" => "bg-green-500",
        "Printer" => "bg-orange-500",
        "Network" or "Server" => "bg-red-500",
        _ => "bg-slate-500"
    };

    public static RenderFragment GetIcon(string deviceType) => deviceType switch
    {
        "Laptop" or "Desktop" => Icon("text-blue-600 dark:text-blue-400", "M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"),
        "Monitor" => Icon("text-purple-600 dark:text-purple-400", "M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"),
        "Phone" => Icon("text-green-600 dark:text-green-400", "M12 18h.01M8 21h8a2 2 0 002-2V5a2 2 0 00-2-2H8a2 2 0 00-2 2v14a2 2 0 002 2z"),
        "Tablet" => Icon("text-green-600 dark:text-green-400", "M12 18h.01M7 21h10a2 2 0 002-2V5a2 2 0 00-2-2H7a2 2 0 00-2 2v14a2 2 0 002 2z"),
        "Printer" => Icon("text-orange-600 dark:text-orange-400", "M17 17h2a2 2 0 002-2v-4a2 2 0 00-2-2H5a2 2 0 00-2 2v4a2 2 0 002 2h2m2 4h6a2 2 0 002-2v-4a2 2 0 00-2-2H9a2 2 0 00-2 2v4a2 2 0 002 2zm8-12V5a2 2 0 00-2-2H9a2 2 0 00-2 2v4h10z"),
        "Network" or "Server" => Icon("text-red-600 dark:text-red-400", "M5 12h14M5 12a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v4a2 2 0 01-2 2M5 12a2 2 0 00-2 2v4a2 2 0 002 2h14a2 2 0 002-2v-4a2 2 0 00-2-2m-2-4h.01M17 16h.01"),
        _ => Icon("text-slate-600 dark:text-slate-400", "M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4")
    };

    private static RenderFragment Icon(string colorClasses, string path) => builder =>
        builder.AddMarkupContent(0,
            $"""<svg class="w-5 h-5 {colorClasses}" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="{path}" /></svg>""");
}
