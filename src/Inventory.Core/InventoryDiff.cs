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
        return changes;
    }

    /// <summary>Mehrere Einträge mit gleichem Schlüssel (z. B. zwei Versionen eines Programms) werden zu einem Wert zusammengefasst.</summary>
    private static Dictionary<string, string?> Group(IEnumerable<(string Key, string? Value)> items) =>
        items.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(
            g => g.Key,
            g => g.Select(i => i.Value).Where(v => !string.IsNullOrEmpty(v)).Distinct().Order().ToList() is { Count: > 0 } values ? string.Join(", ", values) : null,
            StringComparer.OrdinalIgnoreCase);

    private static string? Gigabytes(long? bytes) => bytes is null or 0 ? null : $"{Math.Round(bytes.Value / 1024d / 1024 / 1024)} GB";
}
