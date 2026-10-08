using System.Text;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed record SoftwareSummary(string Name, string? Version, string? Publisher, int Computers);

public sealed record DashboardStats(int Computers, int Online, int Scanned, int ScanErrors, int Packages, int FirewallOff, int AntivirusProblems, int RebootRequired, int WithPendingUpdates, List<(string Os, int Count)> OperatingSystems, List<Deployment> RecentDeployments);

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
        var recent = await db.Deployments.AsNoTracking().Include(d => d.Targets).OrderByDescending(d => d.Id).Take(5).ToListAsync(ct);
        return new DashboardStats(computers.Count, computers.Count(c => c.IsOnline), computers.Count(c => c.LastScanUtc is not null),
            computers.Count(c => c.LastScanError is not null), await db.Packages.CountAsync(ct),
            computers.Count(c => c.FirewallOff), computers.Count(c => c.AntivirusProblem), computers.Count(c => c.UpdateRebootRequired == true),
            computers.Count(c => c.UpdatePendingCount > 0), systems, recent);
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
