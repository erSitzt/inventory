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

public sealed class ReportService(IDbContextFactory<InventoryDbContext> dbFactory, IStringLocalizer<SharedResource>? localizer = null)
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
            RunningDeployments = await db.Deployments.CountAsync(d => d.FinishedUtc == null && d.Targets.Any(t => t.Status == TargetStatus.Running || t.Status == TargetStatus.Queued), ct),
            WaitingOffline = await db.Deployments.Where(d => d.FinishedUtc == null).SelectMany(d => d.Targets).CountAsync(t => t.Status == TargetStatus.WaitingOffline || t.Status == TargetStatus.WaitingReboot, ct),
            ChangesLast24h = await db.InventoryChanges.CountAsync(c => c.TimeUtc >= dayAgo, ct),
            OperatingSystems = systems,
            RecentDeployments = recent,
            RecentChanges = InventoryDiff.MergeUpdates(recentChanges).Select(c => (c, names.GetValueOrDefault(c.ComputerId, "?"))).ToList(),
            RecentAudit = includeAudit ? await db.AuditEntries.AsNoTracking().OrderByDescending(e => e.Id).Take(6).ToListAsync(ct) : [],
        };
    }

    public async Task<string> ComputersCsvAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var csv = new StringBuilder();
        Row(csv, L["Name"], L["Reported name"], L["Online"], L["IP address"], L["Operating system"], L["Version"], L["Feature update"], L["Manufacturer"], L["Model"], L["Serial number"],
            L["Processor"], L["Memory (GB)"], L["User"], L["Last boot (UTC)"], L["Last scan (UTC)"], L["Description"],
            L["Firewall domain"], L["Firewall private"], L["Firewall public"], L["Defender real-time protection"], L["Defender signature (UTC)"], L["Antivirus"],
            L["Last update installation (UTC)"], L["Pending updates"], L["Reboot pending"],
            L["Device type"], L["Firmware"], "Secure Boot", "TPM", L["TPM ready"], L["BitLocker (system drive)"], L["Graphics"], L["Battery health (%)"], L["BIOS date"],
            L["Remote Desktop"], L["Network level authentication"], L["User Account Control"], L["User Account Control level"], "SMB 1", "Credential Guard", L["Memory integrity"], L["LSA protection"],
            L["Built-in administrator account"], L["Built-in administrator: password age (days)"], L["Windows activated"]);
        foreach (var c in await db.Computers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
        {
            Row(csv, c.Name, c.ReportedName, YesNo(c.IsOnline), c.IpAddress, c.OsName, c.OsVersion, c.OsRelease, c.Manufacturer, c.Model, c.SerialNumber,
                c.CpuName, c.MemoryBytes is { } bytes ? Math.Round(bytes / 1024d / 1024 / 1024).ToString() : null, c.LoggedOnUser,
                c.LastBootUtc?.ToString("yyyy-MM-dd HH:mm"), c.LastScanUtc?.ToString("yyyy-MM-dd HH:mm"), c.Description,
                YesNo(c.FirewallDomain), YesNo(c.FirewallPrivate), YesNo(c.FirewallPublic), YesNo(c.DefenderRealTime),
                c.DefenderSignatureUpdatedUtc?.ToString("yyyy-MM-dd HH:mm"), c.AntivirusProducts,
                c.UpdateLastInstallUtc?.ToString("yyyy-MM-dd HH:mm"), c.UpdatePendingCount?.ToString(), YesNo(c.UpdateRebootRequired),
                c.ChassisType is null ? null : L[Chassis.Label(Chassis.Kind(c.ChassisType, c.Model))], c.FirmwareType, YesNo(c.SecureBoot),
                c.TpmPresent == false ? L["none"] : c.TpmVersion, YesNo(c.TpmReady), YesNo(c.SystemDriveProtected), c.Gpu,
                c.BatteryHealthPercent?.ToString(), c.BiosDateUtc?.ToString("yyyy-MM-dd"),
                YesNo(c.RdpEnabled), YesNo(c.RdpNla), YesNo(c.UacEnabled), c.UacLevel is >= 0 and <= 3 ? L[InventoryDiff.UacLevels[c.UacLevel.Value]] : null,
                YesNo(c.Smb1Enabled), YesNo(c.CredentialGuard), YesNo(c.MemoryIntegrity), YesNo(c.LsaProtection),
                YesNo(c.BuiltinAdminEnabled), c.BuiltinAdminPasswordAgeDays?.ToString(), YesNo(c.WindowsActivated));
        }
        return csv.ToString();
    }

    public async Task<string> SoftwareCsvAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Software.AsNoTracking()
            .Join(db.Computers, s => s.ComputerId, c => c.Id, (s, c) => new { Computer = c.Name, s.Name, s.Version, s.Publisher, s.InstallDate, s.Is64Bit, s.PerUser })
            .OrderBy(r => r.Computer).ThenBy(r => r.Name).ToListAsync(ct);
        var csv = new StringBuilder();
        Row(csv, L["Computer"], L["Program"], L["Version"], L["Publisher"], L["Installed"], L["Architecture"], L["Per user"]);
        foreach (var r in rows)
            Row(csv, r.Computer, r.Name, r.Version, r.Publisher, r.InstallDate, r.Is64Bit ? "64 Bit" : "32 Bit", YesNo(r.PerUser));
        return csv.ToString();
    }

    private string? YesNo(bool? value) => value is null ? null : value.Value ? L["yes"] : L["no"];

    /// <summary>Semikolon-getrennt, wie es ein deutsches Excel beim Doppelklick erwartet.</summary>
    internal static void Row(StringBuilder csv, params string?[] values)
    {
        csv.AppendJoin(';', values.Select(Escape)).Append("\r\n");

        static string Escape(string? value)
        {
            value ??= "";
            // Ein führendes =, +, - oder @ würde Excel als Formel ausführen.
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
                value = "'" + value;
            return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }
    }
}
