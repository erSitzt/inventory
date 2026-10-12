using System.Globalization;
using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <param name="Key">Fester Name der Spalte; damit merken sich gespeicherte Berichte ihre Auswahl unabhängig von der Sprache.</param>
/// <param name="Numeric">Die Werte sind Zahlen: sie werden als Zahl sortiert und in Excel als Zahl abgelegt.</param>
public sealed record ReportColumn(string Key, string Title, bool Numeric = false);

public sealed record ReportTable(List<ReportColumn> Columns, List<string?[]> Rows);

/// <param name="Columns">Schlüssel in der gewünschten Reihenfolge; null oder leer = alle Spalten der Quelle.</param>
public sealed record ReportQuery(ReportSource Source, int? CollectionId = null, int? Days = null, IReadOnlyList<string>? Columns = null,
    string? SortColumn = null, bool SortDescending = false);

public sealed partial class ReportService
{
    /// <summary>Ohne Angabe reichen Deployments und Änderungen so viele Tage zurück.</summary>
    public const int DefaultDays = 30;
    /// <summary>Mehr Zeilen liefert kein Bericht; die Tabelle soll sich noch öffnen lassen.</summary>
    public const int MaxRows = 100_000;

    private readonly CollectionService _collections = collectionService ?? new CollectionService(dbFactory, localizer);

    private sealed record Column<T>(string Key, string Title, Func<T, string?> Value, bool Numeric = false);

    private const string Date = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>Alle Spalten einer Quelle in ihrer Standardreihenfolge.</summary>
    public IReadOnlyList<ReportColumn> ColumnsOf(ReportSource source) => source switch
    {
        ReportSource.Computers => ComputerColumns().Select(c => new ReportColumn(c.Key, c.Title, c.Numeric)).ToList(),
        ReportSource.Software => SoftwareColumns().Select(c => new ReportColumn(c.Key, c.Title, c.Numeric)).ToList(),
        ReportSource.PendingUpdates => UpdateColumns().Select(c => new ReportColumn(c.Key, c.Title, c.Numeric)).ToList(),
        ReportSource.Deployments => DeploymentColumns().Select(c => new ReportColumn(c.Key, c.Title, c.Numeric)).ToList(),
        ReportSource.Changes => ChangeColumns().Select(c => new ReportColumn(c.Key, c.Title, c.Numeric)).ToList(),
        _ => [],
    };

    /// <summary>Die Zeilen eines Berichts. Oberfläche, CSV, Excel und der Versand per Zeitplan gehen alle hier durch.</summary>
    public async Task<ReportTable> TableAsync(ReportQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        HashSet<int>? members = query.CollectionId is { } collectionId && Report.HasCollectionFilter(query.Source) ? await _collections.GetMemberIdsAsync(collectionId, ct) : null;
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(query.Days ?? DefaultDays, 1, 3650));

        switch (query.Source)
        {
            case ReportSource.Computers:
            {
                var computers = await db.Computers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
                return Build(ComputerColumns(), computers.Where(c => members is null || members.Contains(c.Id)), query);
            }
            case ReportSource.Software:
            {
                // Erst sortieren, dann in den Zeilentyp füllen: nach einem Konstruktoraufruf kann die Datenbank nicht mehr ordnen.
                var rows = (await db.Software.AsNoTracking()
                        .Join(db.Computers, s => s.ComputerId, c => c.Id, (s, c) => new { c.Id, Computer = c.Name, s.Name, s.Version, s.Publisher, s.InstallDate, s.Is64Bit, s.PerUser })
                        .OrderBy(r => r.Computer).ThenBy(r => r.Name).ToListAsync(ct))
                    .Select(r => new SoftwareRow(r.Id, r.Computer, r.Name, r.Version, r.Publisher, r.InstallDate, r.Is64Bit, r.PerUser));
                return Build(SoftwareColumns(), rows.Where(r => members is null || members.Contains(r.ComputerId)), query);
            }
            case ReportSource.PendingUpdates:
            {
                var rows = (await db.PendingUpdates.AsNoTracking()
                        .Join(db.Computers, u => u.ComputerId, c => c.Id, (u, c) => new { c.Id, Computer = c.Name, c.UpdateLastSearchUtc, Update = u })
                        .OrderBy(r => r.Computer).ThenBy(r => r.Update.Title).ToListAsync(ct))
                    .Select(r => new UpdateRow(r.Id, r.Computer, r.UpdateLastSearchUtc, r.Update));
                return Build(UpdateColumns(), rows.Where(r => members is null || members.Contains(r.ComputerId)), query);
            }
            case ReportSource.Deployments:
            {
                var rows = (await db.Deployments.AsNoTracking().Where(d => d.CreatedUtc >= since).OrderByDescending(d => d.CreatedUtc)
                        .Select(d => new
                        {
                            d.Id, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc, d.StopReason, Total = d.Targets.Count,
                            Succeeded = d.Targets.Count(t => t.Status == TargetStatus.Succeeded || t.Status == TargetStatus.AlreadyInstalled),
                            Failed = d.Targets.Count(t => t.Status == TargetStatus.Failed),
                            Cancelled = d.Targets.Count(t => t.Status == TargetStatus.Cancelled),
                        })
                        .Take(MaxRows).ToListAsync(ct))
                    // Offen ist, was weder gelungen, gescheitert noch abgebrochen ist: in der Warteschlange, laufend oder wartend.
                    .Select(d => new DeploymentRow(d.Id, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc, d.StopReason, d.Total, d.Succeeded, d.Failed, d.Cancelled,
                        d.Total - d.Succeeded - d.Failed - d.Cancelled));
                return Build(DeploymentColumns(), rows, query);
            }
            case ReportSource.Changes:
            {
                var rows = (await db.InventoryChanges.AsNoTracking().Where(c => c.TimeUtc >= since)
                        .Join(db.Computers, c => c.ComputerId, c => c.Id, (change, computer) => new { computer.Id, computer.Name, Change = change })
                        .OrderByDescending(r => r.Change.TimeUtc).ThenByDescending(r => r.Change.Id).Take(MaxRows).ToListAsync(ct))
                    .Select(r => new ChangeRow(r.Id, r.Name, r.Change));
                return Build(ChangeColumns(), rows.Where(r => members is null || members.Contains(r.ComputerId)), query);
            }
            default:
                return new ReportTable([], []);
        }
    }

    private static ReportTable Build<T>(List<Column<T>> all, IEnumerable<T> items, ReportQuery query)
    {
        // Unbekannte Schlüssel (Spalte in einer späteren Version entfernt) fallen still weg; bleibt nichts, gelten alle.
        var chosen = query.Columns is { Count: > 0 }
            ? query.Columns.Select(key => all.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase))).OfType<Column<T>>().Distinct().ToList()
            : all;
        if (chosen.Count == 0)
            chosen = all;

        var source = items;
        // Sortiert wird auch nach einer Spalte, die der Bericht nicht zeigt.
        if (all.FirstOrDefault(c => c.Key.Equals(query.SortColumn, StringComparison.OrdinalIgnoreCase)) is { } sort)
        {
            var comparer = sort.Numeric ? ValueOrder.Numbers : ValueOrder.Text;
            // Leere Werte stehen in beiden Richtungen am Ende.
            var filledFirst = items.OrderBy(item => string.IsNullOrEmpty(sort.Value(item)));
            source = query.SortDescending ? filledFirst.ThenByDescending(sort.Value, comparer) : filledFirst.ThenBy(sort.Value, comparer);
        }
        var rows = source.Take(MaxRows).Select(item => chosen.Select(c => c.Value(item)).ToArray()).ToList();
        return new ReportTable(chosen.Select(c => new ReportColumn(c.Key, c.Title, c.Numeric)).ToList(), rows);
    }

    /// <summary>Zahlen werden als Zahl verglichen, Text ohne Rücksicht auf Groß- und Kleinschreibung.</summary>
    private sealed class ValueOrder(bool numeric) : IComparer<string?>
    {
        public static readonly ValueOrder Text = new(false);
        public static readonly ValueOrder Numbers = new(true);

        public int Compare(string? x, string? y)
        {
            var (emptyX, emptyY) = (string.IsNullOrEmpty(x), string.IsNullOrEmpty(y));
            if (emptyX || emptyY)
                return emptyX == emptyY ? 0 : emptyX ? 1 : -1;
            if (numeric && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                return a.CompareTo(b);
            return StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
        }
    }

    private sealed record SoftwareRow(int ComputerId, string Computer, string Name, string? Version, string? Publisher, string? InstallDate, bool Is64Bit, bool PerUser);
    private sealed record UpdateRow(int ComputerId, string Computer, DateTime? LastSearchUtc, ComputerPendingUpdate Update);
    private sealed record DeploymentRow(int Id, string Package, string? Version, DateTime CreatedUtc, DateTime? FinishedUtc, string? StopReason, int Targets, int Succeeded, int Failed, int Cancelled, int Open);
    private sealed record ChangeRow(int ComputerId, string Computer, InventoryChange Change);

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private List<Column<Computer>> ComputerColumns() =>
    [
        new("name", L["Name"], c => c.Name),
        new("reportedName", L["Reported name"], c => c.ReportedName),
        new("online", L["Online"], c => YesNo(c.IsOnline)),
        new("ip", L["IP address"], c => c.IpAddress),
        new("os", L["Operating system"], c => c.OsName),
        new("osVersion", L["Version"], c => c.OsVersion),
        new("osRelease", L["Feature update"], c => c.OsRelease),
        new("manufacturer", L["Manufacturer"], c => c.Manufacturer),
        new("model", L["Model"], c => c.Model),
        new("serial", L["Serial number"], c => c.SerialNumber),
        new("cpu", L["Processor"], c => c.CpuName),
        new("memoryGb", L["Memory (GB)"], c => c.MemoryBytes is { } bytes ? Math.Round(bytes / 1024d / 1024 / 1024).ToString(CultureInfo.InvariantCulture) : null, Numeric: true),
        new("user", L["User"], c => c.LoggedOnUser),
        new("lastBoot", L["Last boot (UTC)"], c => c.LastBootUtc?.ToString(DateTimeFormat)),
        new("lastScan", L["Last scan (UTC)"], c => c.LastScanUtc?.ToString(DateTimeFormat)),
        new("description", L["Description"], c => c.Description),
        new("firewallDomain", L["Firewall domain"], c => YesNo(c.FirewallDomain)),
        new("firewallPrivate", L["Firewall private"], c => YesNo(c.FirewallPrivate)),
        new("firewallPublic", L["Firewall public"], c => YesNo(c.FirewallPublic)),
        new("defenderRealTime", L["Defender real-time protection"], c => YesNo(c.DefenderRealTime)),
        new("defenderSignature", L["Defender signature (UTC)"], c => c.DefenderSignatureUpdatedUtc?.ToString(DateTimeFormat)),
        new("antivirus", L["Antivirus"], c => c.AntivirusProducts),
        new("lastUpdateInstall", L["Last update installation (UTC)"], c => c.UpdateLastInstallUtc?.ToString(DateTimeFormat)),
        new("pendingUpdates", L["Pending updates"], c => Number(c.UpdatePendingCount), Numeric: true),
        new("pendingSecurityUpdates", L["Pending security updates"], c => Number(c.UpdatePendingSecurityCount), Numeric: true),
        new("rebootPending", L["Reboot pending"], c => YesNo(c.UpdateRebootRequired)),
        new("deviceType", L["Device type"], c => c.ChassisType is null ? null : L[Chassis.Label(Chassis.Kind(c.ChassisType, c.Model))]),
        new("firmware", L["Firmware"], c => c.FirmwareType),
        new("secureBoot", "Secure Boot", c => YesNo(c.SecureBoot)),
        new("tpm", "TPM", c => c.TpmPresent == false ? L["none"] : c.TpmVersion),
        new("tpmReady", L["TPM ready"], c => YesNo(c.TpmReady)),
        new("bitLocker", L["BitLocker (system drive)"], c => YesNo(c.SystemDriveProtected)),
        new("graphics", L["Graphics"], c => c.Gpu),
        new("batteryHealth", L["Battery health (%)"], c => Number(c.BatteryHealthPercent), Numeric: true),
        new("biosDate", L["BIOS date"], c => c.BiosDateUtc?.ToString(Date)),
        new("remoteDesktop", L["Remote Desktop"], c => YesNo(c.RdpEnabled)),
        new("nla", L["Network level authentication"], c => YesNo(c.RdpNla)),
        new("uac", L["User Account Control"], c => YesNo(c.UacEnabled)),
        new("uacLevel", L["User Account Control level"], c => c.UacLevel is >= 0 and <= 3 ? L[InventoryDiff.UacLevels[c.UacLevel.Value]] : null),
        new("smb1", "SMB 1", c => YesNo(c.Smb1Enabled)),
        new("credentialGuard", "Credential Guard", c => YesNo(c.CredentialGuard)),
        new("memoryIntegrity", L["Memory integrity"], c => YesNo(c.MemoryIntegrity)),
        new("lsaProtection", L["LSA protection"], c => YesNo(c.LsaProtection)),
        new("builtinAdmin", L["Built-in administrator account"], c => YesNo(c.BuiltinAdminEnabled)),
        new("builtinAdminPasswordAge", L["Built-in administrator: password age (days)"], c => Number(c.BuiltinAdminPasswordAgeDays), Numeric: true),
        new("activated", L["Windows activated"], c => YesNo(c.WindowsActivated)),
        new("adOu", L["AD organizational unit"], c => c.DirectoryOu),
        new("adGroups", L["AD groups"], c => string.Join(", ", c.DirectoryGroupNames)),
        new("adDescription", L["AD description"], c => c.DirectoryDescription),
        new("adLastLogon", L["Last AD logon (UTC)"], c => c.DirectoryLastLogonUtc?.ToString(DateTimeFormat)),
        new("primaryUser", L["Primary user"], c => c.PrimaryUser),
        new("owner", L["Owner"], c => c.Owner),
        new("location", L["Location"], c => c.Location),
        new("costCenter", L["Cost center"], c => c.CostCenter),
        new("inventoryNumber", L["Inventory number"], c => c.InventoryNumber),
        new("purchaseDate", L["Purchase date"], c => c.PurchaseDate?.ToString(Date)),
        new("warrantyUntil", L["Warranty until"], c => c.WarrantyUntil?.ToString(Date)),
        new("tags", L["Tags"], c => c.Tags),
        new("notes", L["Notes"], c => c.Notes),
    ];

    private List<Column<SoftwareRow>> SoftwareColumns() =>
    [
        new("computer", L["Computer"], r => r.Computer),
        new("program", L["Program"], r => r.Name),
        new("version", L["Version"], r => r.Version),
        new("publisher", L["Publisher"], r => r.Publisher),
        new("installed", L["Installed"], r => r.InstallDate),
        new("architecture", L["Architecture"], r => r.Is64Bit ? "64 Bit" : "32 Bit"),
        new("perUser", L["Per user"], r => YesNo(r.PerUser)),
    ];

    private List<Column<UpdateRow>> UpdateColumns() =>
    [
        new("computer", L["Computer"], r => r.Computer),
        new("kb", "KB", r => r.Update.KbIds),
        new("title", L["Update title"], r => r.Update.Title),
        new("classification", L["Classification"], r => r.Update.Classification is null ? null : L[r.Update.Classification]),
        new("severity", L["Severity"], r => r.Update.Severity is null ? null : L[r.Update.Severity]),
        new("security", L["Security-relevant"], r => YesNo(r.Update.IsSecurity)),
        new("downloaded", L["Downloaded"], r => YesNo(r.Update.IsDownloaded)),
        new("sizeMb", L["Size (MB)"], r => r.Update.SizeBytes > 0 ? Math.Round(r.Update.SizeBytes / 1024d / 1024).ToString(CultureInfo.InvariantCulture) : null, Numeric: true),
        new("lastSearch", L["Last search (UTC)"], r => r.LastSearchUtc?.ToString(DateTimeFormat)),
    ];

    private List<Column<DeploymentRow>> DeploymentColumns() =>
    [
        new("id", L["Number"], r => Number(r.Id), Numeric: true),
        new("package", L["Package"], r => r.Package),
        new("version", L["Version"], r => r.Version),
        new("started", L["Started (UTC)"], r => r.CreatedUtc.ToString(DateTimeFormat)),
        new("finished", L["Finished (UTC)"], r => r.FinishedUtc?.ToString(DateTimeFormat)),
        new("computers", L["Computers"], r => Number(r.Targets), Numeric: true),
        new("succeeded", L["Succeeded"], r => Number(r.Succeeded), Numeric: true),
        new("failed", L["Failed"], r => Number(r.Failed), Numeric: true),
        new("cancelled", L["Cancelled"], r => Number(r.Cancelled), Numeric: true),
        new("open", L["Still open"], r => Number(r.Open), Numeric: true),
        new("stopReason", L["Stopped because"], r => r.StopReason),
    ];

    private List<Column<ChangeRow>> ChangeColumns() =>
    [
        new("time", L["Time (UTC)"], r => r.Change.TimeUtc.ToString(DateTimeFormat)),
        new("computer", L["Computer"], r => r.Computer),
        new("category", L["Area"], r => L[r.Change.Category]),
        new("kind", L["Change"], r => r.Change.Kind switch { ChangeKind.Added => L["new"], ChangeKind.Removed => L["removed"], _ => L["changed"] }),
        new("item", L["Item"], r => r.Change.Item),
        new("old", L["Before"], r => r.Change.OldValue),
        new("new", L["After"], r => r.Change.NewValue),
    ];
}
