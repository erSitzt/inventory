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
        Value("Reported name", previous.ReportedName, current.ComputerName);
        Value("Domain", previous.Domain, current.Domain);
        Value("Operating system", previous.OsName, current.OsName);
        Value("Windows version", previous.OsVersion, current.OsVersion);
        Value("Feature update", previous.OsRelease, current.OsRelease);
        Value("Manufacturer", previous.Manufacturer, current.Manufacturer);
        Value("Model", previous.Model, current.Model);
        Value("Serial number", previous.SerialNumber, current.SerialNumber);
        Value("BIOS", previous.BiosVersion, current.BiosVersion);
        Value("Processor", previous.CpuName, current.CpuName);
        Value("Memory", Gigabytes(previous.MemoryBytes), Gigabytes(current.MemoryBytes));

        if (current.Security is { } security)
        {
            void Security(string item, bool? oldValue, bool? newValue, string on, string off)
            {
                // Erst vergleichen, wenn beide Seiten bekannt sind: der erste Scan mit Sicherheitsstatus ist die Ausgangslage.
                if (oldValue is not null && newValue is not null && oldValue != newValue)
                    Add("Security", ChangeKind.Changed, item, oldValue.Value ? on : off, newValue.Value ? on : off);
            }
            Security("Firewall (domain)", previous.FirewallDomain, security.FirewallDomain, "on", "off");
            Security("Firewall (private)", previous.FirewallPrivate, security.FirewallPrivate, "on", "off");
            Security("Firewall (public)", previous.FirewallPublic, security.FirewallPublic, "on", "off");
            Security("Defender", previous.DefenderEnabled, security.DefenderEnabled, "active", "inactive");
            Security("Defender real-time protection", previous.DefenderRealTime, security.DefenderRealTime, "on", "off");
            Security("Defender tamper protection", previous.DefenderTamperProtected, security.DefenderTamperProtected, "on", "off");
            if (previous.DefenderMode is not null && security.DefenderMode is not null && previous.DefenderMode != security.DefenderMode)
                Add("Security", ChangeKind.Changed, "Defender mode", previous.DefenderMode, security.DefenderMode);
            if (security.Posture is { } posture)
            {
                Security("Remote Desktop", previous.RdpEnabled, posture.RdpEnabled, "on", "off");
                Security("Remote Desktop network level authentication", previous.RdpNla, posture.RdpNla, "on", "off");
                Security("User Account Control", previous.UacEnabled, posture.UacEnabled, "on", "off");
                Security("SMB 1", previous.Smb1Enabled, posture.Smb1Enabled, "on", "off");
                Security("Credential Guard", previous.CredentialGuard, posture.CredentialGuard, "on", "off");
                Security("Memory integrity", previous.MemoryIntegrity, posture.MemoryIntegrity, "on", "off");
                Security("LSA protection", previous.LsaProtection, posture.LsaProtection, "on", "off");
                Security("Built-in administrator account", previous.BuiltinAdminEnabled, posture.BuiltinAdminEnabled, "active", "disabled");
                Security("Windows activation", previous.WindowsActivated, posture.WindowsActivated, "activated", "not activated");
                if (previous.UacLevel is { } oldLevel && posture.UacLevel is { } newLevel && oldLevel != newLevel)
                    Add("Security", ChangeKind.Changed, "User Account Control level", UacLevels[Math.Clamp(oldLevel, 0, 3)], UacLevels[Math.Clamp(newLevel, 0, 3)]);
                // Das Kennwortalter wächst von selbst; eine Aussage ist nur, dass es gesunken ist: das Kennwort wurde geändert.
                if (previous.BuiltinAdminPasswordAgeDays is { } oldAge && posture.BuiltinAdminPasswordAgeDays is { } newAge && newAge < oldAge)
                    Add("Security", ChangeKind.Changed, "Built-in administrator: password age (days)", oldAge.ToString(), newAge.ToString());
            }
            if (previous.AntivirusProducts is not null)
                Set("Antivirus", Split(previous.AntivirusProducts).Select(p => (p, (string?)null)), security.AntivirusProducts.Select(p => (p, (string?)null)));
        }

        Set("Software", previous.Software.Select(s => (s.Name, s.Version)), current.Software.Select(s => (s.Name, s.Version)));
        Set("Hotfix", previous.Hotfixes.Select(h => (h.HotfixId, h.Description)), current.Hotfixes.Select(h => (h.Id, h.Description)));
        Set("Disk", previous.Disks.Select(d => (d.Drive, Gigabytes(d.SizeBytes))), current.Disks.Select(d => (d.Drive, Gigabytes(d.SizeBytes))));
        Set("Network adapter", previous.NetworkAdapters.Select(n => (n.Name, n.MacAddress)), current.NetworkAdapters.Select(n => (n.Name, n.MacAddress)));
        Set("Service",
            previous.Services.Where(s => !PerSessionService().IsMatch(s.Name)).Select(s => (s.Name, s.StartMode)),
            current.Services.Where(s => !PerSessionService().IsMatch(s.Name)).Select(s => (s.Name, s.StartMode)));
        Set("Local user",
            previous.LocalUsers.Select(u => (u.Name, (string?)(u.Disabled ? "disabled" : "active"))),
            current.LocalUsers.Select(u => (u.Name, (string?)(u.Disabled ? "disabled" : "active"))));
        Set("Local administrator",
            previous.LocalAdmins.Select(a => (a.Name, (string?)null)),
            current.LocalAdmins.Select(a => (a.Name, (string?)null)));

        if (current.Hardware is { } hardware)
        {
            // Wie beim Sicherheitsstatus: der erste Scan mit Hardware-Details ist die Ausgangslage.
            void Known(string category, string item, string? oldValue, string? newValue)
            {
                if (oldValue is not null && newValue is not null && !string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
                    Add(category, ChangeKind.Changed, item, oldValue, newValue);
            }
            static string? OnOff(bool? value) => value is null ? null : value.Value ? "on" : "off";
            static string? Tpm(bool? present, bool? ready) => present switch { null => null, false => "none", true => ready == true ? "ready" : "not ready" };
            static string? Module(string? manufacturer, string? partNumber, long capacityBytes) =>
                string.Join(" ", new[] { Gigabytes(capacityBytes), manufacturer, partNumber }.Where(p => !string.IsNullOrWhiteSpace(p)));

            Known("System", "Firmware", previous.FirmwareType, hardware.FirmwareType);
            Known("System", "BIOS date", previous.BiosDateUtc?.ToString("yyyy-MM-dd"), hardware.BiosDateUtc?.ToString("yyyy-MM-dd"));
            Known("System", "Graphics", previous.Gpu, hardware.Gpus.Count == 0 ? null : string.Join(", ", hardware.Gpus.Select(g => g.Name)));
            Known("Security", "Secure Boot", OnOff(previous.SecureBoot), OnOff(hardware.SecureBoot));
            Known("Security", "TPM", Tpm(previous.TpmPresent, previous.TpmReady), Tpm(hardware.TpmPresent, hardware.TpmReady));

            // Listen nur vergleichen, wenn der vorige Scan sie schon erfasst hat; sonst wäre alles "neu".
            if (previous.HardwareScanUtc is not null)
            {
                Set("Memory module",
                    previous.MemoryModules.Select(m => (m.Slot, Module(m.Manufacturer, m.PartNumber, m.CapacityBytes))),
                    hardware.MemoryModules.Select(m => (m.Slot, Module(m.Manufacturer, m.PartNumber, m.CapacityBytes))));
                Set("Physical disk",
                    previous.PhysicalDisks.Select(d => (DiskKey(d.Model, d.SerialNumber), d.Health)),
                    hardware.PhysicalDisks.Select(d => (DiskKey(d.Model, d.SerialNumber), d.Health)));
                Set("BitLocker",
                    previous.Volumes.Select(v => (v.Drive, OnOff(v.Protected))),
                    hardware.Volumes.Select(v => (v.Drive, OnOff(v.Protected))));
                // Monitore fehlen hier bewusst: Notebooks wechseln sie mit jedem Arbeitsplatz.
                Set("Printer",
                    previous.Printers.Select(p => (p.Name, p.Port)),
                    hardware.Printers.Select(p => (p.Name, p.Port)));
            }
        }
        // Lizenzangaben: wie die Hardware erst ab dem zweiten Scan, der sie liefert.
        if (current.Licensing is { } licensing && previous.LicensingScanUtc is not null)
        {
            void Known(string item, string? oldValue, string? newValue)
            {
                if (oldValue is not null && newValue is not null && !string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
                    Add("System", ChangeKind.Changed, item, oldValue, newValue);
            }
            Known("Windows edition", previous.OsEdition, licensing.OsEdition);
            Known("Licence channel", previous.LicenseChannel, licensing.LicenseChannel);
            Known("Processor sockets", previous.CpuSockets?.ToString(), licensing.CpuSockets?.ToString());
            Known("Cluster", previous.ClusterName, licensing.ClusterName);
            if (licensing.ServerFeatures is { } features)
                Set("Server role", previous.ServerFeatures.Select(f => (f.Name, (string?)null)), features.Select(f => (f.Name, (string?)null)));
            if (licensing.SqlInstances is { } instances)
                Set("SQL Server",
                    previous.SqlInstances.Select(i => (i.Name, SqlDescription(i.Edition, i.Version))),
                    instances.Select(i => (i.Name, SqlDescription(i.Edition, i.Version))));
            if (licensing.VirtualMachines is { } machines)
                Set("Virtual machine",
                    previous.VirtualMachines.Select(v => (v.Name, v.VirtualCpus?.ToString())),
                    machines.Select(v => (v.Name, v.VirtualCpus?.ToString())));
        }
        return MergeUpdates(changes);
    }

    private static string? SqlDescription(string? edition, string? version) =>
        string.Join(" ", new[] { edition, version }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Englische Bezeichnungen der Stufen der Benutzerkontensteuerung; zugleich Schlüssel für die Übersetzung.</summary>
    public static readonly string[] UacLevels = ["never notify", "notify without dimming the desktop", "default level", "always notify"];

    /// <summary>Modell plus Seriennummer: zwei gleiche Geräte bleiben unterscheidbar, ein Tausch wird sichtbar.</summary>
    private static string DiskKey(string model, string? serialNumber) =>
        string.IsNullOrWhiteSpace(serialNumber) ? model : $"{model} ({serialNumber})";

    /// <summary>
    /// Viele Programme tragen die Version im Namen; ein Update erscheint dann als "entfernt" plus "neu". Solche Paare
    /// innerhalb eines Scans werden zu einer Änderung "alte Version → neue Version" zusammengefasst.
    /// </summary>
    public static List<InventoryChange> MergeUpdates(List<InventoryChange> changes)
    {
        var result = new List<InventoryChange>(changes);
        var groups = changes
            .Where(c => c.Category == "Software" && c.Kind != ChangeKind.Changed)
            .GroupBy(c => (c.ComputerId, c.TimeUtc, Key: SoftwareNames.Key(c.Item, c.OldValue ?? c.NewValue)));
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
