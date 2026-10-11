using System.Text.RegularExpressions;

namespace Inventory.Core;

public static partial class PendingUpdates
{
    [GeneratedRegex(@"\bKB\d{6,8}\b", RegexOptions.IgnoreCase)]
    private static partial Regex KbNumber();

    /// <summary>KB-Nummern aus dem Titel, für Scans älterer Runner, die nur Titel liefern.</summary>
    public static List<string> KbIdsFromTitle(string title) =>
        KbNumber().Matches(title).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

    /// <summary>
    /// Sicherheitsrelevant sind Sicherheits- und kritische Updates sowie alles mit einem MSRC-Schweregrad. Ohne
    /// Klassifizierung (ältere Runner) entscheidet der Titel: kumulative und Sicherheitsupdates nennen sich so.
    /// </summary>
    public static bool IsSecurity(string? classification, string? severity, string title)
    {
        if (!string.IsNullOrEmpty(severity) || classification is "Security Updates" or "Critical Updates")
            return true;
        if (classification is not null)
            return false;
        return new[] { "Security", "Sicherheit", "Cumulative", "Kumulativ" }.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
