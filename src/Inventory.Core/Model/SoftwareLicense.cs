namespace Inventory.Core.Model;

/// <summary>Eine Lizenz, deren Bestand mit den gefundenen Installationen verglichen wird.</summary>
public class SoftwareLicense
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>
    /// Welche Programme zählen: ohne Stern genügt, dass der Programmname den Text enthält; mit Stern muss der ganze Name
    /// auf das Muster passen (wie bei der Sammlungsregel "Software"). Groß- und Kleinschreibung spielt keine Rolle.
    /// </summary>
    public required string Pattern { get; set; }
    /// <summary>So viele Lizenzen sind vorhanden. Gezählt wird je Rechner, nicht je Benutzer.</summary>
    public int Owned { get; set; }
    public decimal? CostPerLicense { get; set; }
    /// <summary>Ablauf oder Verlängerung; null = unbefristet.</summary>
    public DateOnly? ValidUntil { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public static bool Matches(string pattern, string programName)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return false;
        if (!pattern.Contains('*'))
            return programName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.IsMatch(programName,
            "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
