using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public class TrendTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Set(DateTimeOffset value) => _now = value;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task One_value_per_day_is_recorded_and_old_values_are_removed()
    {
        using var db = new TestDb();
        // Ein Mittwoch.
        var clock = new Clock(new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.Zero));
        var trends = new TrendService(db, new ReportService(db), clock);
        var computers = await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01 pc-02 pc-03");
        await using (var context = db.CreateDbContext())
        {
            await context.Computers.Where(c => c.Id == computers[0].Id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.SystemDriveProtected, false).SetProperty(c => c.UpdatePendingCount, 3).SetProperty(c => c.LastScanError, "Zugriff verweigert"));
            context.DailySnapshots.Add(new DailySnapshot { Date = new DateOnly(2025, 1, 1), Computers = 1 });
            await context.SaveChangesAsync();
        }

        Assert.True(await trends.TakeIfDueAsync());
        Assert.False(await trends.TakeIfDueAsync());
        // Ein zweiter Dienst (nach einem Neustart) schreibt am selben Tag nicht noch einmal.
        Assert.False(await new TrendService(db, new ReportService(db), clock).TakeIfDueAsync());

        var today = (await trends.TrendsAsync()).Days.Single();
        Assert.Equal((new DateOnly(2026, 10, 14), 3, 1, 1, 1), (today.Date, today.Computers, today.DriveUnprotected, today.WithPendingUpdates, today.ScanErrors));

        clock.Advance(TimeSpan.FromDays(1));
        await using (var context = db.CreateDbContext())
            await context.Computers.Where(c => c.Id == computers[1].Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatePendingCount, 1));
        Assert.True(await trends.TakeIfDueAsync());

        // Der Zeitraum umfasst genau 30 Tage einschließlich heute.
        await using (var context = db.CreateDbContext())
        {
            context.DailySnapshots.Add(new DailySnapshot { Date = new DateOnly(2026, 10, 15).AddDays(-TrendService.TrendDays), WithPendingUpdates = 99 });
            context.DailySnapshots.Add(new DailySnapshot { Date = new DateOnly(2026, 10, 15).AddDays(-(TrendService.TrendDays - 1)), WithPendingUpdates = 7 });
            await context.SaveChangesAsync();
        }
        Assert.Equal([7, 1, 2], (await trends.TrendsAsync()).Days.Select(d => d.WithPendingUpdates));
        await using (var context = db.CreateDbContext())
            await context.DailySnapshots.Where(s => s.WithPendingUpdates >= 7).ExecuteDeleteAsync();

        var days = (await trends.TrendsAsync()).Days;
        Assert.Equal([1, 2], days.Select(d => d.WithPendingUpdates));
        await using (var context = db.CreateDbContext())
            Assert.Equal(2, await context.DailySnapshots.CountAsync());
    }

    [Fact]
    public async Task Deployments_are_counted_per_week_and_scans_by_age()
    {
        using var db = new TestDb();
        // Mittwoch, 14.10.2026: die laufende Woche beginnt am Montag, dem 12.
        var clock = new Clock(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var trends = new TrendService(db, new ReportService(db), clock);
        var now = clock.GetUtcNow().UtcDateTime;
        await using (var context = db.CreateDbContext())
        {
            Deployment At(DateTime created, params TargetStatus[] targets) => new()
            {
                PackageName = "Editor", CreatedUtc = created,
                Targets = targets.Select((status, i) => new DeploymentTarget { ComputerName = $"pc-{i}", Status = status }).ToList(),
            };
            context.Deployments.AddRange(
                At(now.AddDays(-1), TargetStatus.Succeeded, TargetStatus.AlreadyInstalled, TargetStatus.Failed, TargetStatus.Cancelled, TargetStatus.WaitingOffline),
                At(new DateTime(2026, 10, 11, 23, 0, 0, DateTimeKind.Utc), TargetStatus.Failed),
                At(now.AddDays(-7 * TrendService.Weeks - 3), TargetStatus.Succeeded));
            foreach (var (name, age) in new (string, double?)[] { ("a", 0.2), ("b", 2), ("c", 5), ("d", 20), ("e", 90), ("f", null) })
                context.Computers.Add(new Computer { Name = name, LastScanUtc = age is { } days ? now.AddDays(-days) : null });
            await context.SaveChangesAsync();
        }

        var result = await trends.TrendsAsync();

        Assert.Equal(TrendService.Weeks, result.Weeks.Count);
        Assert.Equal(new DeploymentWeek(new DateOnly(2026, 10, 12), 2, 2), result.Weeks[^1]);
        // Sonntagabend gehört noch zur Vorwoche; was älter als der Zeitraum ist, zählt nicht.
        Assert.Equal(new DeploymentWeek(new DateOnly(2026, 10, 5), 0, 1), result.Weeks[^2]);
        Assert.Equal(5, result.Weeks.Sum(w => w.Succeeded + w.Failed));
        Assert.Equal([1, 1, 1, 1, 1, 1], result.ScanAges.Select(a => a.Computers));
        Assert.Equal("never", result.ScanAges[^1].Label);
    }
}
