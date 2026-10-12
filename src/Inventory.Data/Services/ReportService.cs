using System.Text;
using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="PerMachine">Rechner, auf denen das Programm für alle Benutzer installiert ist.</param>
/// <param name="PerUser">Rechner, auf denen es nur im Profil eines Benutzers installiert ist.</param>
public sealed record SoftwareSummary(string Name, string? Version, string? Publisher, int Computers, int PerMachine = 0, int PerUser = 0);

/// <summary>Ein Rechner mit einem bestimmten Programm und der Art, wie es dort installiert ist.</summary>
public sealed record SoftwareInstall(Computer Computer, bool PerMachine, bool PerUser, bool CarriedOver);

/// <summary>Ein Programm über alle Versionen hinweg; <see cref="Versions"/> enthält je Name/Version eine Zeile.</summary>
public sealed record SoftwareGroup(string Name, string? Publisher, int Computers, List<SoftwareSummary> Versions)
{
    public bool PerMachine => Versions.Any(v => v.PerMachine > 0);
    public bool PerUser => Versions.Any(v => v.PerUser > 0);
}

/// <summary>Ein ausstehendes Update über alle Rechner hinweg.</summary>
/// <param name="Key">KB-Nummern, sonst der Titel; damit lassen sich die betroffenen Rechner abfragen.</param>
/// <param name="ComputerNames">Namen der Rechner, auf denen das Update aussteht.</param>
public sealed record PendingUpdateSummary(string Key, string? KbIds, string Title, string? Classification, string? Severity, bool IsSecurity, int Computers, List<string> ComputerNames);

/// <summary>Patch-Stand: was wo aussteht und wie viele Rechner ohne offene Sicherheitsupdates sind.</summary>
/// <param name="Computers">Alle Rechner, deren Update-Stand bekannt ist.</param>
public sealed record PatchCompliance(List<PendingUpdateSummary> Updates, List<Computer> Computers)
{
    public int Compliant => Computers.Count(c => c.UpdatePendingSecurityCount == 0);
}

/// <summary>Kennzahlen der Übersichtsseite.</summary>
public sealed class DashboardStats
{
    public int Computers { get; init; }
    public int Online { get; init; }
    public int Scanned { get; init; }
    public int ScanErrors { get; init; }
    public int Unscanned { get; init; }
    /// <summary>Gescannt, aber länger als <see cref="StaleDays"/> Tage her.</summary>
    public int StaleScans { get; init; }
    public int Packages { get; init; }
    public int Schedules { get; init; }
    public int Agents { get; init; }
    public int AgentsConnected { get; init; }
    public int FirewallOff { get; init; }
    public int AntivirusProblems { get; init; }
    public int RebootRequired { get; init; }
    public int WithPendingUpdates { get; init; }
    public int NameMismatch { get; init; }
    public int MissingInDirectory { get; init; }
    /// <summary>Garantie läuft innerhalb von <see cref="WarrantyWarningDays"/> Tagen ab oder ist abgelaufen.</summary>
    public int WarrantyEnding { get; init; }
    public const int WarrantyWarningDays = 90;
    public int RunningDeployments { get; init; }
    public int WaitingOffline { get; init; }
    public int ChangesLast24h { get; init; }
    public List<(string Os, int Count)> OperatingSystems { get; init; } = [];
    public List<Deployment> RecentDeployments { get; init; } = [];
    public List<(InventoryChange Change, string Computer)> RecentChanges { get; init; } = [];
    public List<AuditEntry> RecentAudit { get; init; } = [];

    public const int StaleDays = 7;

    /// <summary>Rechner, bei denen etwas zu tun ist (ein Rechner kann mehrfach zählen).</summary>
    public int AttentionItems => FirewallOff + AntivirusProblems + RebootRequired + WithPendingUpdates + ScanErrors + NameMismatch + MissingInDirectory;
}

public sealed partial class ReportService(IDbContextFactory<InventoryDbContext> dbFactory, IStringLocalizer<SharedResource>? localizer = null, CollectionService? collectionService = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    /// <summary>Jede Kombination aus Programm und Version mit der Zahl der Rechner, auf denen sie installiert ist.</summary>
    public async Task<List<SoftwareSummary>> SoftwareAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Software.AsNoTracking().Select(s => new { s.Name, s.Version, s.Publisher, s.ComputerId, s.PerUser }).ToListAsync(ct);
        return rows.GroupBy(s => (s.Name, s.Version))
            .Select(g => new SoftwareSummary(g.Key.Name, g.Key.Version, g.Select(s => s.Publisher).FirstOrDefault(p => p is not null), g.Select(s => s.ComputerId).Distinct().Count()))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Version)
            .ToList();
    }

    /// <summary>Programme zusammengefasst über Versionen ("7-Zip 24.08" und "7-Zip 26.04"), Jahreszahlen bleiben getrennt ("Office 2019" / "Office 2022").</summary>
    public async Task<List<SoftwareGroup>> SoftwareGroupsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Software.AsNoTracking().Select(s => new { s.Name, s.Version, s.Publisher, s.ComputerId, s.PerUser }).ToListAsync(ct);
        return rows.GroupBy(s => SoftwareNames.Key(s.Name, s.Version))
            .Select(group =>
            {
                var versions = group.GroupBy(s => (s.Name, s.Version))
                    .Select(g => new SoftwareSummary(g.Key.Name, g.Key.Version, g.Select(s => s.Publisher).FirstOrDefault(p => p is not null), g.Select(s => s.ComputerId).Distinct().Count(),
                        g.Where(s => !s.PerUser).Select(s => s.ComputerId).Distinct().Count(), g.Where(s => s.PerUser).Select(s => s.ComputerId).Distinct().Count()))
                    .OrderByDescending(v => v.Version, VersionOrder.Instance).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                // Der häufigste gemeldete Name, ohne Version, steht für die Gruppe.
                var typical = group.GroupBy(s => s.Name).OrderByDescending(g => g.Count()).First().First();
                return new SoftwareGroup(SoftwareNames.Display(typical.Name, typical.Version), versions.Select(v => v.Publisher).FirstOrDefault(p => p is not null),
                    group.Select(s => s.ComputerId).Distinct().Count(), versions);
            })
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private sealed class VersionOrder : IComparer<string?>
    {
        public static readonly VersionOrder Instance = new();
        public int Compare(string? x, string? y) => Inventory.Contracts.VersionComparer.Compare(x ?? "", y ?? "");
    }

    public async Task<List<SoftwareInstall>> ComputersWithSoftwareAsync(string name, string? version, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.AsNoTracking()
            .Include(c => c.Software.Where(s => s.Name == name && s.Version == version))
            .Where(c => c.Software.Any(s => s.Name == name && s.Version == version))
            .OrderBy(c => c.Name).ToListAsync(ct);
        return computers.Select(c => new SoftwareInstall(c, c.Software.Any(s => !s.PerUser), c.Software.Any(s => s.PerUser), c.Software.Any(s => s.CarriedOver))).ToList();
    }

    /// <param name="includeAudit">false = ohne die letzten Protokolleinträge (für Rollen, die das Protokoll nicht lesen dürfen).</param>
    public async Task<DashboardStats> DashboardAsync(CancellationToken ct = default, bool includeAudit = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.AsNoTracking().ToListAsync(ct);
        var systems = computers.Where(c => c.OsName is not null)
            .GroupBy(c => $"{c.OsName!.Replace("Microsoft ", "")} {c.OsRelease}".Trim())
            .Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ToList();
        var recent = await db.Deployments.AsNoTracking().Include(d => d.Targets).OrderByDescending(d => d.Id).Take(6).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var dayAgo = now.AddDays(-1);
        var names = computers.ToDictionary(c => c.Id, c => c.Name);
        var recentChanges = await db.InventoryChanges.AsNoTracking().OrderByDescending(c => c.Id).Take(8).ToListAsync(ct);
        return new DashboardStats
        {
            Computers = computers.Count,
            Online = computers.Count(c => c.IsOnline),
            Scanned = computers.Count(c => c.LastScanUtc is not null),
            ScanErrors = computers.Count(c => c.LastScanError is not null),
            Unscanned = computers.Count(c => c.LastScanUtc is null),
            StaleScans = computers.Count(c => c.LastScanUtc is { } scan && now - scan > TimeSpan.FromDays(DashboardStats.StaleDays)),
            Packages = await db.Packages.CountAsync(ct),
            Schedules = await db.Schedules.CountAsync(s => s.Enabled, ct),
            Agents = computers.Count(c => c.AgentKeyHash is not null),
            AgentsConnected = computers.Count(c => c.AgentLastSeenUtc is { } seen && now - seen < TimeSpan.FromMinutes(5)),
            FirewallOff = computers.Count(c => c.FirewallOff),
            AntivirusProblems = computers.Count(c => c.AntivirusProblem),
            RebootRequired = computers.Count(c => c.UpdateRebootRequired == true),
            WithPendingUpdates = computers.Count(c => c.UpdatePendingCount > 0),
            NameMismatch = computers.Count(c => c.NameMismatch),
            MissingInDirectory = computers.Count(c => c.MissingInDirectory),
            WarrantyEnding = computers.Count(c => c.WarrantyDaysLeft(DateOnly.FromDateTime(DateTime.Now)) <= DashboardStats.WarrantyWarningDays),
            RunningDeployments = await db.Deployments.CountAsync(d => d.FinishedUtc == null && d.Targets.Any(t => t.Status == TargetStatus.Running || t.Status == TargetStatus.Queued), ct),
            WaitingOffline = await db.Deployments.Where(d => d.FinishedUtc == null).SelectMany(d => d.Targets).CountAsync(t => t.Status == TargetStatus.WaitingOffline || t.Status == TargetStatus.WaitingReboot, ct),
            ChangesLast24h = await db.InventoryChanges.CountAsync(c => c.TimeUtc >= dayAgo, ct),
            OperatingSystems = systems,
            RecentDeployments = recent,
            RecentChanges = InventoryDiff.MergeUpdates(recentChanges).Select(c => (c, names.GetValueOrDefault(c.ComputerId, "?"))).ToList(),
            RecentAudit = includeAudit ? await db.AuditEntries.AsNoTracking().OrderByDescending(e => e.Id).Take(6).ToListAsync(ct) : [],
        };
    }

    public async Task<string> ComputersCsvAsync(CancellationToken ct = default) => ReportExport.Csv(await TableAsync(new ReportQuery(ReportSource.Computers), ct));

    public async Task<string> SoftwareCsvAsync(CancellationToken ct = default) => ReportExport.Csv(await TableAsync(new ReportQuery(ReportSource.Software), ct));

    public async Task<PatchCompliance> PatchComplianceAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PendingUpdates.AsNoTracking().ToListAsync(ct);
        var names = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var updates = rows.GroupBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Ältere Runner kennen Klassifizierung und Schweregrad nicht: die vollständigste Meldung beschreibt das Update.
                var best = g.OrderByDescending(u => u.Classification is not null).ThenByDescending(u => u.Severity is not null).First();
                return new PendingUpdateSummary(g.Key, best.KbIds, best.Title, best.Classification, best.Severity, g.Any(u => u.IsSecurity), g.Select(u => u.ComputerId).Distinct().Count(),
                    g.Select(u => names.GetValueOrDefault(u.ComputerId) ?? "").Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList());
            })
            .OrderByDescending(u => u.IsSecurity).ThenByDescending(u => u.Computers).ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var computers = await db.Computers.AsNoTracking().Where(c => c.UpdatePendingCount != null).OrderBy(c => c.Name).ToListAsync(ct);
        return new PatchCompliance(updates, computers);
    }

    /// <param name="key"><see cref="PendingUpdateSummary.Key"/>.</param>
    public async Task<List<Computer>> ComputersMissingUpdateAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.PendingUpdates.Where(u => u.KbIds == key || (u.KbIds == null && u.Title == key)).Select(u => u.ComputerId).Distinct().ToListAsync(ct);
        return await db.Computers.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<string> PendingUpdatesCsvAsync(CancellationToken ct = default) => ReportExport.Csv(await TableAsync(new ReportQuery(ReportSource.PendingUpdates), ct));

    private string? YesNo(bool? value) => value is null ? null : value.Value ? L["yes"] : L["no"];
}
