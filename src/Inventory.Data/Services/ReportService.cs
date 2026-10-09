using System.Text;
using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed record SoftwareSummary(string Name, string? Version, string? Publisher, int Computers);

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

public sealed class ReportService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    /// <summary>Jede Kombination aus Programm und Version mit der Zahl der Rechner, auf denen sie installiert ist.</summary>
    public async Task<List<SoftwareSummary>> SoftwareAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Software.AsNoTracking().Select(s => new { s.Name, s.Version, s.Publisher, s.ComputerId }).ToListAsync(ct);
        return rows.GroupBy(s => (s.Name, s.Version))
            .Select(g => new SoftwareSummary(g.Key.Name, g.Key.Version, g.Select(s => s.Publisher).FirstOrDefault(p => p is not null), g.Select(s => s.ComputerId).Distinct().Count()))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Version)
            .ToList();
    }

    public async Task<List<Computer>> ComputersWithSoftwareAsync(string name, string? version, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Computers.AsNoTracking()
            .Where(c => c.Software.Any(s => s.Name == name && s.Version == version))
            .OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<DashboardStats> DashboardAsync(CancellationToken ct = default)
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
            WaitingOffline = await db.Deployments.Where(d => d.FinishedUtc == null).SelectMany(d => d.Targets).CountAsync(t => t.Status == TargetStatus.WaitingOffline, ct),
            ChangesLast24h = await db.InventoryChanges.CountAsync(c => c.TimeUtc >= dayAgo, ct),
            OperatingSystems = systems,
            RecentDeployments = recent,
            RecentChanges = InventoryDiff.MergeUpdates(recentChanges).Select(c => (c, names.GetValueOrDefault(c.ComputerId, "?"))).ToList(),
            RecentAudit = await db.AuditEntries.AsNoTracking().OrderByDescending(e => e.Id).Take(6).ToListAsync(ct),
        };
    }

    public async Task<string> ComputersCsvAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var csv = new StringBuilder();
        Row(csv, "Name", "Gemeldeter Name", "Online", "IP-Adresse", "Betriebssystem", "Version", "Funktionsupdate", "Hersteller", "Modell", "Seriennummer",
            "Prozessor", "Arbeitsspeicher (GB)", "Benutzer", "Letzter Start (UTC)", "Letzter Scan (UTC)", "Beschreibung",
            "Firewall Domäne", "Firewall Privat", "Firewall Öffentlich", "Defender Echtzeitschutz", "Defender-Signatur (UTC)", "Virenschutz",
            "Letzte Update-Installation (UTC)", "Ausstehende Updates", "Neustart ausstehend");
        foreach (var c in await db.Computers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
        {
            Row(csv, c.Name, c.ReportedName, c.IsOnline ? "ja" : "nein", c.IpAddress, c.OsName, c.OsVersion, c.OsRelease, c.Manufacturer, c.Model, c.SerialNumber,
                c.CpuName, c.MemoryBytes is { } bytes ? Math.Round(bytes / 1024d / 1024 / 1024).ToString() : null, c.LoggedOnUser,
                c.LastBootUtc?.ToString("yyyy-MM-dd HH:mm"), c.LastScanUtc?.ToString("yyyy-MM-dd HH:mm"), c.Description,
                YesNo(c.FirewallDomain), YesNo(c.FirewallPrivate), YesNo(c.FirewallPublic), YesNo(c.DefenderRealTime),
                c.DefenderSignatureUpdatedUtc?.ToString("yyyy-MM-dd HH:mm"), c.AntivirusProducts,
                c.UpdateLastInstallUtc?.ToString("yyyy-MM-dd HH:mm"), c.UpdatePendingCount?.ToString(), YesNo(c.UpdateRebootRequired));
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
        Row(csv, "Rechner", "Programm", "Version", "Hersteller", "Installiert", "Architektur", "Pro Benutzer");
        foreach (var r in rows)
            Row(csv, r.Computer, r.Name, r.Version, r.Publisher, r.InstallDate, r.Is64Bit ? "64 Bit" : "32 Bit", r.PerUser ? "ja" : "nein");
        return csv.ToString();
    }

    private static string? YesNo(bool? value) => value is null ? null : value.Value ? "ja" : "nein";

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
