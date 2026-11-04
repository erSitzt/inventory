using System.Globalization;

namespace Inventory.Core;

/// <summary>
/// Datumsangaben, wie Windows sie im Inventar liefert: Win32_QuickFixEngineering.InstalledOn immer amerikanisch ("10/26/2023"), auf
/// alten Systemen auch als FILETIME in Hex; InstallDate der Software in der Registry als "20231026".
/// </summary>
public static class InventoryDates
{
    private static readonly string[] Formats = ["yyyyMMdd", "M/d/yyyy", "yyyy-MM-dd", "M/d/yyyy h:mm:ss tt"];

    /// <summary>Das Datum; null, wenn der Text keines ist.</summary>
    public static DateOnly? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            return DateOnly.FromDateTime(date);
        if (text.Length == 16 && long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime) && fileTime > 0)
        {
            try { return DateOnly.FromDateTime(DateTime.FromFileTimeUtc(fileTime)); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return null;
    }

    /// <summary>Zur Anzeige im Format der Sprache ("26.10.2023"); was kein Datum ist, bleibt wie geliefert.</summary>
    public static string? Format(string? text) => Parse(text) is { } date ? date.ToString("d", CultureInfo.CurrentCulture) : text;

    /// <summary>Für Berichte, API und MCP: "2023-10-26"; was kein Datum ist, bleibt wie geliefert.</summary>
    public static string? Iso(string? text) => Parse(text) is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : text;
}
