namespace AssetDesk.Web.Components.UI;

/// <summary>One entry in a <c>MultiSelect</c> list. <paramref name="Value"/> is the stable key
/// (usually an id as a string); <paramref name="Label"/> is what the user sees.</summary>
public record MultiSelectOption(string Value, string Label);
