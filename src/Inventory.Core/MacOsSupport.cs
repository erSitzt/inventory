using System.Text.RegularExpressions;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>Wie lange ein Mac noch macOS-Versionen und Sicherheitsupdates bekommt.</summary>
/// <param name="FinalMajor">Die letzte macOS-Version, die das Modell bekommt; null bei Apple Silicon (bekommt die aktuelle).</param>
/// <param name="RunningMajor">Die installierte Version, z. B. "15" oder "10.15"; null, wenn unbekannt.</param>
/// <param name="UpgradeTo">Eine neuere Version, auf die sich aktualisieren lässt; null, wenn keine.</param>
/// <param name="SecurityUpdates">Ob die installierte Version noch Sicherheitsupdates bekommt (Schätzung); null, wenn unbekannt.</param>
/// <param name="SecurityUntilYear">Bis etwa wann (Herbst dieses Jahres) die installierte Version noch Updates bekommt; null, wenn unbekannt.</param>
public sealed record MacOsAssessment(bool AppleSilicon, string? FinalMajor, string? RunningMajor, string? UpgradeTo, bool? SecurityUpdates, int? SecurityUntilYear);

/// <summary>
/// macOS-Unterstützung je Mac-Modell. Intel-Macs: die letzte Version laut Apples Kompatibilitätslisten (macOS 26 ist die letzte
/// für Intel). Apple Silicon: bekommt die aktuelle Version; Grenzen für künftige Versionen sind hier nicht bekannt.
/// Sicherheitsupdates: Apple versorgt erfahrungsgemäß die aktuelle Version und die zwei davor – eine Beobachtung, keine Zusage.
/// </summary>
public static partial class MacOsSupport
{
    /// <summary>Die neueste macOS-Version; anheben, wenn eine neue erscheint (meldet ein Mac schon eine neuere, gilt die).</summary>
    public const string LatestMajor = "27";

    /// <summary>Versionen in der Reihenfolge ihres Erscheinens, jeweils im Herbst des genannten Jahres.</summary>
    private static readonly (string Major, int Year)[] Releases =
    [
        ("10.13", 2017), ("10.14", 2018), ("10.15", 2019), ("11", 2020), ("12", 2021), ("13", 2022), ("14", 2023), ("15", 2024),
        ("26", 2025), ("27", 2026), ("28", 2027), ("29", 2028), ("30", 2029),
    ];

    /// <summary>Letzte macOS-Version je Intel-Modell: (Familie, Hauptnummer) für eine Generation, mit Unternummer für Ausnahmen.</summary>
    private static readonly Dictionary<(string Family, int Major, int? Minor), string> IntelFinal = new()
    {
        // MacBook Pro
        [("MacBookPro", 16, 1)] = "26", [("MacBookPro", 16, 4)] = "26",  // 16 Zoll, 2019
        [("MacBookPro", 16, 2)] = "26",                                  // 13 Zoll, 2020, vier Thunderbolt-3-Anschlüsse
        [("MacBookPro", 16, 3)] = "15",                                  // 13 Zoll, 2020, zwei Thunderbolt-3-Anschlüsse
        [("MacBookPro", 15, null)] = "15",                               // 2018/2019
        [("MacBookPro", 14, null)] = "13",                               // 2017
        [("MacBookPro", 13, null)] = "12",                               // 2016
        [("MacBookPro", 12, null)] = "12",                               // Anfang 2015, 13 Zoll
        [("MacBookPro", 11, 4)] = "12", [("MacBookPro", 11, 5)] = "12",  // Mitte 2015, 15 Zoll
        [("MacBookPro", 11, null)] = "11",                               // Ende 2013 / Mitte 2014
        [("MacBookPro", 10, null)] = "10.15", [("MacBookPro", 9, null)] = "10.15",
        // MacBook Air
        [("MacBookAir", 9, null)] = "15",                                // 2020, Intel
        [("MacBookAir", 8, null)] = "14",                                // 2018/2019
        [("MacBookAir", 7, null)] = "12",                                // 2015/2017
        [("MacBookAir", 6, null)] = "11",                                // 2013/2014
        [("MacBookAir", 5, null)] = "10.15",                             // 2012
        // MacBook
        [("MacBook", 10, null)] = "13", [("MacBook", 9, null)] = "12", [("MacBook", 8, null)] = "11",
        // iMac
        [("iMac", 20, null)] = "26",                                     // 2020
        [("iMac", 19, null)] = "15",                                     // 2019
        [("iMac", 18, null)] = "13",                                     // 2017
        [("iMac", 17, null)] = "12", [("iMac", 16, null)] = "12",        // Ende 2015
        [("iMac", 15, null)] = "11",                                     // Retina 5K, Ende 2014 / Mitte 2015
        [("iMac", 14, 4)] = "11",                                        // Mitte 2014
        [("iMac", 14, null)] = "10.15", [("iMac", 13, null)] = "10.15",
        [("iMacPro", 1, null)] = "15",                                   // 2017
        // Mac mini
        [("Macmini", 8, null)] = "15",                                   // 2018
        [("Macmini", 7, null)] = "12",                                   // Ende 2014
        [("Macmini", 6, null)] = "10.15",                                // Ende 2012
        // Mac Pro
        [("MacPro", 7, null)] = "26",                                    // 2019
        [("MacPro", 6, null)] = "12",                                    // Ende 2013
        [("MacPro", 5, null)] = "10.14",
    };

    /// <summary>"15.6.1 (24G90)" → "15", "10.15.7" → "10.15"; null, wenn es keine Version ist.</summary>
    public static string? Major(string? version)
    {
        var parts = (version ?? "").Split(' ')[0].Split('.');
        if (!int.TryParse(parts[0], out _))
            return null;
        return parts[0] == "10" && parts.Length > 1 ? $"10.{parts[1]}" : parts[0];
    }

    private static int Index(string? major) => major is null ? -1 : Array.FindIndex(Releases, r => r.Major == major);

    /// <summary>Die neuere von <see cref="LatestMajor"/> und den Versionen, die Macs schon melden.</summary>
    public static string Latest(IEnumerable<string?> runningVersions) =>
        runningVersions.Select(Major).Where(m => Index(m) > Index(LatestMajor)).OrderBy(Index).LastOrDefault() ?? LatestMajor;

    /// <summary>Die Modellkennung aus der Modellangabe des Agenten, z. B. "MacBook Pro (16-inch, 2021) (MacBookPro18,1)".</summary>
    public static string? ModelIdentifier(string? model) =>
        ModelPattern().Match(model ?? "") is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"\(([A-Za-z]+\d+,\d+)\)\s*$")]
    private static partial Regex ModelPattern();

    /// <summary>Letzte macOS-Version eines Intel-Modells; null, wenn das Modell nicht bekannt ist.</summary>
    public static string? FinalForIntel(string? identifier)
    {
        var match = Regex.Match(identifier ?? "", @"^([A-Za-z]+)(\d+),(\d+)$");
        if (!match.Success)
            return null;
        var (family, major, minor) = (match.Groups[1].Value, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        return IntelFinal.GetValueOrDefault((family, major, minor)) ?? IntelFinal.GetValueOrDefault((family, major, null));
    }

    /// <summary>Bis etwa zu welchem Jahr eine Version Sicherheitsupdates bekommt: bis die dritte neuere erscheint.</summary>
    public static int? SecurityUntil(string? major)
    {
        var index = Index(major);
        return index < 0 || index + 3 >= Releases.Length ? null : Releases[index + 3].Year;
    }

    /// <summary>Die Einschätzung für einen Mac; null für andere Rechner und Intel-Macs, deren Modell nicht bekannt ist.</summary>
    /// <param name="latestMajor">Die neueste macOS-Version, siehe <see cref="Latest"/>.</param>
    public static MacOsAssessment? Assess(Computer computer, string latestMajor = LatestMajor)
    {
        if (!computer.IsMac)
            return null;
        var appleSilicon = computer.CpuName?.StartsWith("Apple", StringComparison.OrdinalIgnoreCase) == true;
        var final = appleSilicon ? null : FinalForIntel(ModelIdentifier(computer.Model));
        if (!appleSilicon && final is null)
            return null;
        var running = Major(computer.OsVersion);
        var target = final ?? latestMajor;
        var upgrade = Index(running) >= 0 && Index(running) < Index(target) ? target : null;
        bool? security = Index(running) < 0 ? null : Index(running) >= Index(latestMajor) - 2;
        return new MacOsAssessment(appleSilicon, final, Index(running) < 0 ? null : running, upgrade, security, SecurityUntil(running));
    }
}
