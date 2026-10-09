using System.Text.RegularExpressions;


namespace Inventory.Data.Services;

/// <summary>
/// Macht aus den Freitexten des Protokolls anklickbare Verweise: Rechner-, Paket-, Sammlungs- und Zeitplannamen führen
/// zur jeweiligen Seite, "#12" bei Deployments zum Deployment. Die Einträge selbst bleiben reiner Text.
/// </summary>
public sealed class AuditLinkMap
{
    public sealed record Segment(string Text, string? Href);

    private readonly Dictionary<string, string> _targets;
    private readonly Regex? _pattern;

    private AuditLinkMap(Dictionary<string, string> targets)
    {
        _targets = targets;
        if (targets.Count > 0)
        {
            // Längste Namen zuerst, damit "7-Zip (x64)" nicht an "7-Zip" scheitert; Namen dürfen nicht mitten in einem Wort stehen.
            var names = targets.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape);
            _pattern = new Regex($@"(?<!\w)(?:{string.Join("|", names)})(?!\w)|#\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    public static async Task<AuditLinkMap> LoadAsync(ComputerService computers, PackageService packages, CollectionService collections, ScheduleService schedules, CancellationToken ct = default)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, string href)
        {
            if (!string.IsNullOrWhiteSpace(name) && name.Length > 1)
                targets.TryAdd(name, href);
        }
        // Rechner zuerst: bei gleichem Namen gewinnt der Rechner, er ist im Protokoll am häufigsten gemeint.
        foreach (var c in await computers.ListAsync(ct))
        {
            Add(c.Name, $"computers/{c.Id}");
            Add(c.Name.Split('.')[0], $"computers/{c.Id}");
        }
        foreach (var p in await packages.ListAsync(ct))
            Add(p.Name, $"packages/{p.Id}");
        foreach (var s in await collections.ListAsync(ct))
            Add(s.Collection.Name, $"computers?collection={s.Collection.Id}");
        foreach (var s in await schedules.ListAsync(ct))
            Add(s.Name, "schedules");
        return new AuditLinkMap(targets);
    }

    /// <param name="action">Aktion des Eintrags; "#12" führt nur bei Deployment-Einträgen zum Deployment.</param>
    public IReadOnlyList<Segment> Segments(string? text, string action)
    {
        if (string.IsNullOrEmpty(text) || _pattern is null)
            return text is null ? [] : [new Segment(text, null)];
        var result = new List<Segment>();
        var last = 0;
        foreach (Match match in _pattern.Matches(text))
        {
            string? href;
            if (match.Value[0] == '#')
                href = action.Contains("Deployment", StringComparison.OrdinalIgnoreCase) ? $"deployments/{match.Value[1..]}" : null;
            else
                _targets.TryGetValue(match.Value, out href);
            if (href is null)
                continue;
            if (match.Index > last)
                result.Add(new Segment(text[last..match.Index], null));
            result.Add(new Segment(match.Value, href));
            last = match.Index + match.Length;
        }
        if (last < text.Length)
            result.Add(new Segment(text[last..], null));
        return result;
    }
}
