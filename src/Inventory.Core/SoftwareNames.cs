using System.Text.RegularExpressions;

namespace Inventory.Core;

/// <summary>
/// Fasst Programmnamen über Versionen hinweg zusammen: "7-Zip 24.08 (x64)" und "7-Zip 26.04 (x64)" sind dasselbe Programm,
/// "Office 2019" und "Office 2022" dagegen nicht. Als Version gilt nur eine Zahl mit Punkt (1.2, 3.12.1, v2.0) oder die
/// gemeldete Versionsnummer selbst; eine einzelne Jahreszahl oder Generation bleibt Teil des Namens.
/// </summary>
public static partial class SoftwareNames
{
    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionInName();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>Vergleichsschlüssel ohne Version, Groß-/Kleinschreibung und Trennzeichen.</summary>
    public static string Key(string name, string? version)
    {
        var stripped = Strip(name, version);
        return string.Join(' ', stripped.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    /// <summary>Anzeigename ohne Version, sonst wie gemeldet: "7-Zip 24.08 (x64)" → "7-Zip (x64)".</summary>
    public static string Display(string name, string? version)
    {
        var stripped = Spaces().Replace(Strip(name, version), " ").Replace("( ", "(").Replace(" )", ")").Replace("()", "").Trim(' ', '-', '–', ',');
        return stripped.Length > 0 ? stripped : name;
    }

    private static string Strip(string name, string? version)
    {
        if (!string.IsNullOrEmpty(version) && !version.Contains(','))
            name = name.Replace(version, " ", StringComparison.OrdinalIgnoreCase);
        return VersionInName().Replace(name, " ");
    }
}
