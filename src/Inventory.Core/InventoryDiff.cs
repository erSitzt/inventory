using System.Text.RegularExpressions;
using Inventory.Contracts;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>Ermittelt, was sich zwischen dem gespeicherten Stand eines Rechners und einem neuen Scan geändert hat.</summary>
public static partial class InventoryDiff
{
    /// <summary>Dienste, die Windows je Anmeldung mit wechselndem Suffix anlegt (z. B. AarSvc_1c86fa), wären reines Rauschen.</summary>
    [GeneratedRegex("_[0-9a-f]{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PerSessionService();

    /// <param name="previous">Rechner mit geladenen Inventar-Listen.</param>
    public static List<InventoryChange> Compare(Computer previous, InventoryData current, DateTime timeUtc)
    {
        var changes = new List<InventoryChange>();
        // Der erste Scan ist die Ausgangslage, kein Unterschied.
        if (previous.LastScanUtc is null)
            return changes;

        void Add(string category, ChangeKind kind, string item, string? oldValue, string? newValue) => changes.Add(new InventoryChange
        {
            ComputerId = previous.Id, TimeUtc = timeUtc, Category = category, Kind = kind, Item = item, OldValue = oldValue, NewValue = newValue,
        });

        void Value(string item, string? oldValue, string? newValue)
        {
            if (!string.Equals(oldValue ?? "", newValue ?? "", StringComparison.OrdinalIgnoreCase))
                Add("System", ChangeKind.Changed, item, oldValue, newValue);
        }

        // oldItems/newItems: Schlüssel → Vergleichswert. Ein geänderter Wert bei gleichem Schlüssel zählt als Änderung.
        void Set(string category, IEnumerable<(string Key, string? Value)> oldItems, IEnumerable<(string Key, string? Value)> newItems)
        {
            var before = Group(oldItems);
            var after = Group(newItems);
            foreach (var (key, value) in after)
            {
                if (!before.TryGetValue(key, out var old))
                    Add(category, ChangeKind.Added, key, null, value);
                else if (!string.Equals(old ?? "", value ?? "", StringComparison.OrdinalIgnoreCase))
                    Add(category, ChangeKind.Changed, key, old, value);
            }
            foreach (var (key, value) in before.Where(b => !after.ContainsKey(b.Key)))
                Add(category, ChangeKind.Removed, key, value, null);
        }

        // Nur wenn schon ein Scan den Namen geliefert hat: sonst stammt der bisherige Wert aus der Erreichbarkeitsprüfung.
        Value("Gemeldeter Name", previous.ReportedName, current.ComputerName);
        Value("Domäne", previous.Domain, current.Domain);
        Value("Betriebssystem", previous.OsName, current.OsName);
        Value("Windows-Version", previous.OsVersion, current.OsVersion);
        Value("Funktionsupdate", previous.OsRelease, current.OsRelease);
        Value("Hersteller", previous.Manufacturer, current.Manufacturer);
        Value("Modell", previous.Model, current.Model);
        Value("Seriennummer", previous.SerialNumber, current.SerialNumber);
        Value("BIOS", previous.BiosVersion, current.BiosVersion);
        Value("Prozessor", previous.CpuName, current.CpuName);
        Value("Arbeitsspeicher", Gigabytes(previous.MemoryBytes), Gigabytes(current.MemoryBytes));

        if (current.Security is { } security)
        {
            void Security(string item, bool? oldValue, bool? newValue, string on, string off)
            {
                // Erst vergleichen, wenn beide Seiten bekannt sind: der erste Scan mit Sicherheitsstatus ist die Ausgangslage.
                if (oldValue is not null && newValue is not null && oldValue != newValue)
                    Add("Sicherheit", ChangeKind.Changed, item, oldValue.Value ? on : off, newValue.Value ? on : off);
            }
            Security("Firewall (Domäne)", previous.FirewallDomain, security.FirewallDomain, "an", "aus");
            Security("Firewall (Privat)", previous.FirewallPrivate, security.FirewallPrivate, "an", "aus");
            Security("Firewall (Öffentlich)", previous.FirewallPublic, security.FirewallPublic, "an", "aus");
            Security("Defender", previous.DefenderEnabled, security.DefenderEnabled, "aktiv", "inaktiv");
            Security("Defender-Echtzeitschutz", previous.DefenderRealTime, security.DefenderRealTime, "an", "aus");
            Security("Defender-Manipulationsschutz", previous.DefenderTamperProtected, security.DefenderTamperProtected, "an", "aus");
            if (previous.AntivirusProducts is not null)
                Set("Virenschutz", Split(previous.AntivirusProducts).Select(p => (p, (string?)null)), security.AntivirusProducts.Select(p => (p, (string?)null)));
        }

        Set("Software", previous.Software.Select(s => (s.Name, s.Version)), current.Software.Select(s => (s.Name, s.Version)));
        Set("Hotfix", previous.Hotfixes.Select(h => (h.HotfixId, h.Description)), current.Hotfixes.Select(h => (h.Id, h.Description)));
        Set("Datenträger", previous.Disks.Select(d => (d.Drive, Gigabytes(d.SizeBytes))), current.Disks.Select(d => (d.Drive, Gigabytes(d.SizeBytes))));
        Set("Netzwerkadapter", previous.NetworkAdapters.Select(n => (n.Name, n.MacAddress)), current.NetworkAdapters.Select(n => (n.Name, n.MacAddress)));
        Set("Dienst",
            previous.Services.Where(s => !PerSessionService().IsMatch(s.Name)).Select(s => (s.Name, s.StartMode)),
            current.Services.Where(s => !PerSessionService().IsMatch(s.Name)).Select(s => (s.Name, s.StartMode)));
        Set("Lokaler Benutzer",
            previous.LocalUsers.Select(u => (u.Name, (string?)(u.Disabled ? "deaktiviert" : "aktiv"))),
            current.LocalUsers.Select(u => (u.Name, (string?)(u.Disabled ? "deaktiviert" : "aktiv"))));
        Set("Lokaler Administrator",
            previous.LocalAdmins.Select(a => (a.Name, (string?)null)),
            current.LocalAdmins.Select(a => (a.Name, (string?)null)));
        return MergeUpdates(changes);
    }

    /// <summary>Versionsnummern im Programmnamen, z. B. "7-Zip 24.08 (x64)" oder "Python 3.12.1".</summary>
    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionInName();

    /// <summary>
    /// Viele Programme tragen die Version im Namen; ein Update erscheint dann als "entfernt" plus "neu". Solche Paare
    /// innerhalb eines Scans werden zu einer Änderung "alte Version → neue Version" zusammengefasst.
    /// </summary>
    public static List<InventoryChange> MergeUpdates(List<InventoryChange> changes)
    {
        var result = new List<InventoryChange>(changes);
        var groups = changes
            .Where(c => c.Category == "Software" && c.Kind != ChangeKind.Changed)
            .GroupBy(c => (c.ComputerId, c.TimeUtc, Key: NameWithoutVersion(c.Item, c.OldValue ?? c.NewValue)));
        foreach (var group in groups)
        {
            // Nur eindeutige Paare: bei mehreren Kandidaten ließe sich nicht sagen, welche Version welche ersetzt hat.
            if (group.Key.Key.Length == 0 || group.Count() != 2
                || group.SingleOrDefault(c => c.Kind == ChangeKind.Removed) is not { } removed || group.SingleOrDefault(c => c.Kind == ChangeKind.Added) is not { } added)
                continue;
            result[result.IndexOf(added)] = new InventoryChange
            {
                Id = added.Id, ComputerId = added.ComputerId, TimeUtc = added.TimeUtc, Category = added.Category, Kind = ChangeKind.Changed,
                Item = added.Item, OldValue = removed.OldValue ?? removed.Item, NewValue = added.NewValue ?? added.Item,
            };
            result.Remove(removed);
        }
        return result;
    }

    private static string NameWithoutVersion(string name, string? version)
    {
        if (!string.IsNullOrEmpty(version) && !version.Contains(','))
            name = name.Replace(version, " ", StringComparison.OrdinalIgnoreCase);
        name = VersionInName().Replace(name, " ");
        return string.Join(' ', name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    /// <summary>Mehrere Einträge mit gleichem Schlüssel (z. B. zwei Versionen eines Programms) werden zu einem Wert zusammengefasst.</summary>
    private static Dictionary<string, string?> Group(IEnumerable<(string Key, string? Value)> items) =>
        items.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(
            g => g.Key,
            g => g.Select(i => i.Value).Where(v => !string.IsNullOrEmpty(v)).Distinct().Order().ToList() is { Count: > 0 } values ? string.Join(", ", values) : null,
            StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Split(string products) =>
        products.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Gigabytes(long? bytes) => bytes is null or 0 ? null : $"{Math.Round(bytes.Value / 1024d / 1024 / 1024)} GB";
}
