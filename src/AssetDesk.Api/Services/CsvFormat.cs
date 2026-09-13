namespace AssetDesk.Api.Services;

/// <summary>
/// One CSV escaping rule for every export. Lifted out of ReportsController when the licence report
/// became its second caller, rather than copied into it.
/// </summary>
public static class CsvFormat
{
    /// <summary>Quotes a field containing a comma, quote or line break, doubling any quotes inside it.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";

        return value;
    }
}
