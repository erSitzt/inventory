using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <param name="WeekStart">Montag der Woche (Ortszeit des Servers).</param>
/// <param name="Succeeded">Rechner, auf denen ein in dieser Woche gestartetes Deployment gelang oder nichts zu tun hatte.</param>
public sealed record DeploymentWeek(DateOnly WeekStart, int Succeeded, int Failed);

/// <param name="Label">Schlüssel für die Übersetzung, z. B. "today".</param>
public sealed record ScanAgeBucket(string Label, int Computers);

public sealed record DashboardTrends(List<DailySnapshot> Days, List<DeploymentWeek> Weeks, List<ScanAgeBucket> ScanAges);

/// <summary>Hält die Kennzahlen der Übersicht einmal täglich fest und liefert die Reihen für die Diagramme.</summary>
public sealed class TrendService(IDbContextFactory<InventoryDbContext> dbFactory, ReportService reports, TimeProvider time)
{
    /// <summary>So lange bleiben die Tageswerte erhalten.</summary>
    public const int RetentionDays = 400;
    public const int TrendDays = 30;
    public const int Weeks = 8;

    private DateOnly? _lastTaken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime);

    /// <summary>Schreibt den Wert des heutigen Tages, falls es ihn noch nicht gibt. Billig, wenn nichts zu tun ist.</summary>
    public async Task<bool> TakeIfDueAsync(CancellationToken ct = default)
    {
        var today = Today;
        if (_lastTaken == today)
            return false;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.DailySnapshots.AnyAsync(s => s.Date == today, ct))
            {
                _lastTaken = today;
                return false;
            }
        }
        await TakeAsync(ct);
        return true;
    }

    /// <summary>Schreibt oder ersetzt den Wert des heutigen Tages und räumt alte Werte ab.</summary>
    public async Task<DailySnapshot> TakeAsync(CancellationToken ct = default)
    {
        var today = Today;
        var stats = await reports.DashboardAsync(ct, includeAudit: false);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.DailySnapshots.FirstOrDefaultAsync(s => s.Date == today, ct);
        if (snapshot is null)
        {
            snapshot = new DailySnapshot { Date = today };
            db.DailySnapshots.Add(snapshot);
        }
        snapshot.Computers = stats.Computers;
        snapshot.Online = stats.Online;
        snapshot.Scanned = stats.Scanned;
        snapshot.ScanErrors = stats.ScanErrors;
        snapshot.StaleScans = stats.StaleScans;
        snapshot.FirewallOff = stats.FirewallOff;
        snapshot.AntivirusProblems = stats.AntivirusProblems;
        snapshot.RebootRequired = stats.RebootRequired;
        snapshot.WithPendingUpdates = stats.WithPendingUpdates;
        snapshot.WarrantyEnding = stats.WarrantyEnding;
        snapshot.Agents = stats.Agents;
        snapshot.DriveUnprotected = await db.Computers.CountAsync(c => c.SystemDriveProtected == false, ct);
        await db.SaveChangesAsync(ct);

        var oldest = today.AddDays(-RetentionDays);
        await db.DailySnapshots.Where(s => s.Date < oldest).ExecuteDeleteAsync(ct);
        _lastTaken = today;
        return snapshot;
    }

    public async Task<DashboardTrends> TrendsAsync(CancellationToken ct = default)
    {
        var today = Today;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var from = today.AddDays(-TrendDays);
        var days = await db.DailySnapshots.AsNoTracking().Where(s => s.Date >= from).OrderBy(s => s.Date).ToListAsync(ct);

        // Wochen beginnen am Montag der Ortszeit; gezählt wird nach dem Start des Deployments.
        var thisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstWeek = thisWeek.AddDays(-7 * (Weeks - 1));
        var sinceUtc = TimeZoneInfo.ConvertTimeToUtc(firstWeek.ToDateTime(TimeOnly.MinValue), time.LocalTimeZone);
        var targets = await db.DeploymentTargets.AsNoTracking()
            .Join(db.Deployments.Where(d => d.CreatedUtc >= sinceUtc), t => t.DeploymentId, d => d.Id, (t, d) => new { d.CreatedUtc, t.Status }).ToListAsync(ct);
        var weeks = Enumerable.Range(0, Weeks).Select(i => firstWeek.AddDays(7 * i)).Select(start =>
        {
            var inWeek = targets.Where(t =>
            {
                var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t.CreatedUtc, DateTimeKind.Utc), time.LocalTimeZone));
                return local >= start && local < start.AddDays(7);
            }).ToList();
            return new DeploymentWeek(start, inWeek.Count(t => t.Status is TargetStatus.Succeeded or TargetStatus.AlreadyInstalled), inWeek.Count(t => t.Status == TargetStatus.Failed));
        }).ToList();

        var now = time.GetUtcNow().UtcDateTime;
        var scans = await db.Computers.AsNoTracking().Select(c => c.LastScanUtc).ToListAsync(ct);
        int Count(double fromDays, double toDays) => scans.Count(s => s is { } scan && (now - scan).TotalDays >= fromDays && (now - scan).TotalDays < toDays);
        var ages = new List<ScanAgeBucket>
        {
            new("up to 1 day", Count(double.MinValue, 1)),
            new("1 to 3 days", Count(1, 3)),
            new("3 to 7 days", Count(3, 7)),
            new("7 to 30 days", Count(7, 30)),
            new("more than 30 days", Count(30, double.MaxValue)),
            new("never", scans.Count(s => s is null)),
        };
        return new DashboardTrends(days, weeks, ages);
    }
}
