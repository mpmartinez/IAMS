namespace AssetDesk.Web.Components.UI;

/// <summary>
/// Class strings for the handful of places that cannot use a UI component but must still look
/// like one. A file picker is the case that keeps coming up: the control that opens an
/// &lt;InputFile&gt; has to be the &lt;label for&gt; itself, so it cannot be a Button.
/// </summary>
public static class UiClasses
{
    /// <summary>Mirrors Button's outline variant at its default size.</summary>
    public const string OutlineButton =
        "inline-flex items-center justify-center gap-2 h-9 px-4 py-2 rounded-md border border-input " +
        "bg-background shadow-sm text-sm font-medium cursor-pointer transition-colors " +
        "hover:bg-accent hover:text-accent-foreground";

    /// <summary>Mirrors Button's default variant at its default size, for an &lt;a&gt; that acts as one.</summary>
    public const string PrimaryButton =
        "inline-flex items-center justify-center gap-2 h-9 px-4 py-2 rounded-md bg-primary " +
        "text-primary-foreground shadow text-sm font-medium transition-colors hover:bg-primary/90";

    /// <summary>Mirrors Button's default variant at Size="lg", for an &lt;a&gt; that acts as one.</summary>
    public const string PrimaryButtonLarge =
        "inline-flex items-center justify-center gap-2 h-10 px-8 rounded-md bg-primary " +
        "text-primary-foreground shadow text-sm font-medium transition-colors hover:bg-primary/90";

    /// <summary>Mirrors Button's outline variant at Size="lg".</summary>
    public const string OutlineButtonLarge =
        "inline-flex items-center justify-center gap-2 h-10 px-8 rounded-md border border-input " +
        "bg-background shadow-sm text-sm font-medium cursor-pointer transition-colors " +
        "hover:bg-accent hover:text-accent-foreground";
}
