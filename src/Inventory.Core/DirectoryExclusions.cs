using System.Text.RegularExpressions;

namespace Inventory.Core;

/// <summary>
/// Ausschlussregeln der AD-Synchronisation, eine je Zeile: ein Container (OU=Test,DC=corp,DC=example, samt allem darunter)
/// oder ein Namensmuster mit * und ? (KIOSK-*).
/// </summary>
public sealed class DirectoryExclusions
{
    public IReadOnlyList<string> Containers { get; }
    public IReadOnlyList<string> Patterns { get; }
    private readonly List<Regex> _patterns;

    private DirectoryExclusions(List<string> containers, List<string> patterns)
    {
        Containers = containers;
        Patterns = patterns;
        _patterns = [.. patterns.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))];
    }

    public bool IsEmpty => Containers.Count == 0 && Patterns.Count == 0;

    /// <summary>Zeilen (oder durch ; getrennt); Kommas trennen nicht, sie gehören zum DN. Eine Angabe mit = ist ein Container.</summary>
    public static DirectoryExclusions Parse(string? text)
    {
        var entries = (text ?? "").Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new([.. entries.Where(e => e.Contains('='))], [.. entries.Where(e => !e.Contains('='))]);
    }

    /// <summary>Container, die nicht wie ein DN aussehen (kein DC=), für einen Hinweis in den Einstellungen.</summary>
    public IEnumerable<string> Suspicious => Containers.Where(c => !c.Contains("DC=", StringComparison.OrdinalIgnoreCase));

    /// <param name="name">Kontoname (sAMAccountName ohne $ bzw. cn).</param>
    /// <param name="distinguishedName">DN des Kontos; die Container-Regeln greifen nur damit.</param>
    public bool Excludes(string name, string? distinguishedName) =>
        _patterns.Any(p => p.IsMatch(name) || p.IsMatch(name.Split('.')[0]))
        || Containers.Any(c => DirectoryNames.IsWithin(distinguishedName, c));
}
