using System.Text.RegularExpressions;

namespace Inventory.Core.Licensing;

/// <summary>Eine Version eines Produkts mit ihrem Erscheinungsdatum (Beginn des Lebenszyklus laut Microsoft).</summary>
/// <param name="Year">Jahreszahl im Namen, z. B. 2025; danach wird verglichen.</param>
/// <param name="Build">Windows Server: Build des Betriebssystems, z. B. 20348 für 2022.</param>
/// <param name="Major">SQL Server: Hauptversion, z. B. 16 für 2022.</param>
public sealed record ProductVersion(int Year, DateOnly Released, int? Build = null, int? Major = null);

/// <summary>Bedarf eines Produkts in einer Version; null = ohne Versionsangabe (jede Lizenz deckt ihn).</summary>
public sealed record LicenseNeed(string Product, int? Version, int Cores);

/// <summary>
/// Versionen von Windows Server und SQL Server. Eine Lizenz deckt ihre Version und ältere (Downgrade-Recht), nie neuere. Mit Software
/// Assurance kommt jede Version hinzu, die während der Laufzeit erscheint; nach dem Ende bleibt das Recht auf die letzte davon.
/// </summary>
public static partial class LicenseVersions
{
    [GeneratedRegex(@"(?<!\d)(20\d\d)(?!\d)")]
    private static partial Regex YearPattern();

    /// <summary>Die neueste bekannte Version; null, wenn die Regeln keine Versionen kennen.</summary>
    public static int? Newest(IReadOnlyList<ProductVersion> versions) => versions.Count == 0 ? null : versions.Max(v => v.Year);

    /// <summary>Die neueste Version, die bis einschließlich zu diesem Tag erschienen ist.</summary>
    public static int? LatestReleased(IReadOnlyList<ProductVersion> versions, DateOnly day) =>
        versions.Where(v => v.Released <= day).Select(v => (int?)v.Year).Max();

    /// <summary>
    /// Version von Windows Server aus der Build-Nummer (z. B. "10.0.20348.2700"), sonst aus dem Namen ("Windows Server 2022 Standard").
    /// "2019 or later" (so meldet vSphere manche Gäste) ist keine Version: null.
    /// </summary>
    public static int? WindowsServer(string? osName, string? osVersion, IReadOnlyList<ProductVersion> versions)
    {
        var parts = (osVersion ?? "").Split('.');
        if (parts.Length >= 3 && int.TryParse(parts[2], out var build) && build > 0
            && versions.Where(v => v.Build is { } b && b <= build).MaxBy(v => v.Build) is { } byBuild)
            return byBuild.Year;
        if (osName is null || osName.Contains("or later", StringComparison.OrdinalIgnoreCase))
            return null;
        return YearPattern().Match(osName) is { Success: true } match ? Known(int.Parse(match.Groups[1].Value), versions) : null;
    }

    /// <summary>Eine Jahreszahl nach der neuesten bekannten Version ist keine bekannte Version: null, damit sie als unbekannt zählt.</summary>
    private static int? Known(int year, IReadOnlyList<ProductVersion> versions) => Newest(versions) is { } newest && year > newest ? null : year;

    /// <summary>Version von SQL Server aus der Hauptversion (16 = 2022); eine unbekannte Hauptversion ist null.</summary>
    public static int? Sql(int? major, IReadOnlyList<ProductVersion> versions) =>
        major is { } m ? versions.FirstOrDefault(v => v.Major == m)?.Year : null;

    /// <summary>
    /// Die eingetragene Version eines Bestands: eine Jahreszahl ("2022", "Windows Server 2022"), bei SQL Server auch die Hauptversion
    /// ("16"). null, wenn nichts zu erkennen ist.
    /// </summary>
    public static int? Parse(string? text, IReadOnlyList<ProductVersion> versions)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (YearPattern().Match(text) is { Success: true } match)
            return Known(int.Parse(match.Groups[1].Value), versions);
        return int.TryParse(text.Trim().Split('.')[0], out var major) ? Sql(major, versions) : null;
    }

    /// <summary>
    /// Bis zu welcher Version ein Bestand reicht: seine Version, mit Software Assurance zusätzlich jede Version, die bis zum Ende der SA
    /// (höchstens heute) erschienen ist. null = ohne Version, deckt jede.
    /// </summary>
    public static int? Covers(int? own, DateOnly? softwareAssuranceUntil, DateOnly today, IReadOnlyList<ProductVersion> versions)
    {
        if (own is null || softwareAssuranceUntil is not { } until)
            return own;
        var latest = LatestReleased(versions, until < today ? until : today);
        return latest is { } year && year > own ? year : own;
    }
}

/// <summary>
/// Der Bestand je Produkt und Version, zum Verbrauchen bei der Berechnung. Ein Bestand deckt Bedarf seiner Version und älterer; ein
/// Bestand ohne Version deckt jeden, ein Bedarf ohne Version wird von jedem gedeckt.
/// </summary>
public sealed class LicenseStock
{
    private sealed class Pool(string product, int? version, int remaining)
    {
        public string Product { get; } = product;
        public int? Version { get; } = version;
        public int Remaining { get; set; } = remaining;
    }

    private readonly List<Pool> _pools;

    public LicenseStock(IEnumerable<(string Product, int? Version, int Quantity)> entries) =>
        _pools = entries.Where(e => e.Quantity > 0).Select(e => new Pool(e.Product, e.Version, e.Quantity)).ToList();

    /// <summary>Bestand ohne Versionen, je Produkt eine Menge.</summary>
    public static implicit operator LicenseStock(Dictionary<string, int> stock) => new(stock.Select(s => (s.Key, (int?)null, s.Value)));

    public static LicenseStock Empty => new([]);

    public LicenseStock Clone() => new(_pools.Select(p => (p.Product, p.Version, p.Remaining)));

    private IEnumerable<Pool> Covering(string product, int? version) => _pools
        .Where(p => p.Remaining > 0 && string.Equals(p.Product, product, StringComparison.OrdinalIgnoreCase) && (version is null || p.Version is null || p.Version >= version))
        // Die kleinste passende Version zuerst, ohne Version zuletzt: neuere Lizenzen bleiben für Bedarf, den nur sie decken.
        .OrderBy(p => p.Version is null).ThenBy(p => p.Version);

    /// <summary>Was vom Produkt für diese Version noch da ist.</summary>
    public int Available(string product, int? version) => Covering(product, version).Sum(p => p.Remaining);

    /// <summary>Verbraucht bis zu <paramref name="cores"/> und gibt zurück, wie viel gedeckt war.</summary>
    public int Take(string product, int? version, int cores)
    {
        var taken = 0;
        foreach (var pool in Covering(product, version).ToList())
        {
            var used = Math.Min(pool.Remaining, cores - taken);
            pool.Remaining -= used;
            taken += used;
            if (taken == cores)
                break;
        }
        return taken;
    }
}
