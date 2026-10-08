using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed class ScheduleService(IDbContextFactory<InventoryDbContext> dbFactory, CollectionService collections, ScanService scans, DeploymentService deployments, TimeProvider time)
{
    public async Task<List<Schedule>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Schedules.AsNoTracking().Include(s => s.Package).Include(s => s.Collection).OrderBy(s => s.Name).ToListAsync(ct);
    }

    /// <summary>Speichert den Zeitplan und berechnet den nächsten Lauf neu.</summary>
    public async Task SaveAsync(Schedule schedule, CancellationToken ct = default)
    {
        schedule.Package = null;
        schedule.Collection = null;
        schedule.Name = schedule.Name.Trim();
        if (schedule.Action == ScheduleAction.Scan)
            schedule.PackageId = null;
        schedule.NextRunUtc = schedule.Enabled ? NextUtc(schedule) : null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Schedules.Update(schedule);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Schedules.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Führt alle fälligen Zeitpläne aus; liefert deren Anzahl.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        List<Schedule> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            due = await db.Schedules.Where(s => s.Enabled && s.NextRunUtc != null && s.NextRunUtc <= now).ToListAsync(ct);

        foreach (var schedule in due)
        {
            // Erst den nächsten Termin festschreiben: ein Absturz mitten im Lauf darf nicht zur Endlosschleife werden.
            schedule.LastRunUtc = now;
            schedule.NextRunUtc = NextUtc(schedule);
            schedule.LastResult = "läuft";
            await StoreAsync(schedule, ct);

            schedule.LastResult = await RunAsync(schedule, ct);
            await StoreAsync(schedule, ct);
        }
        return due.Count;
    }

    /// <summary>Führt den Zeitplan sofort aus, ohne den regulären Rhythmus zu verändern.</summary>
    public async Task<string> RunNowAsync(int id, CancellationToken ct = default)
    {
        Schedule schedule;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            schedule = await db.Schedules.FirstAsync(s => s.Id == id, ct);
        var result = await RunAsync(schedule, ct);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.Schedules.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastResult, result), ct);
        return result;
    }

    private async Task<string> RunAsync(Schedule schedule, CancellationToken ct)
    {
        try
        {
            List<int> ids;
            if (schedule.CollectionId is { } collectionId)
            {
                ids = (await collections.GetMemberIdsAsync(collectionId, ct)).ToList();
            }
            else
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                ids = await db.Computers.Select(c => c.Id).ToListAsync(ct);
            }
            if (ids.Count == 0)
                return "Keine Rechner in der Sammlung";

            if (schedule.Action == ScheduleAction.Scan)
            {
                var outcomes = await scans.ScanAsync(ids, null, ct);
                var failed = outcomes.Count(o => !o.Success);
                return failed == 0 ? $"{outcomes.Count} Rechner gescannt" : $"{outcomes.Count - failed} gescannt, {failed} fehlgeschlagen";
            }

            if (schedule.PackageId is not { } packageId)
                return "Kein Paket gewählt";
            var deployment = await deployments.StartAsync(packageId, ids, schedule.ScanAfter, schedule.RetryOffline, ct);
            return $"Deployment #{deployment.Id} auf {deployment.Targets.Count} Rechner gestartet";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return $"Fehler: {ex.Message}";
        }
    }

    private DateTime? NextUtc(Schedule schedule)
    {
        var zone = time.LocalTimeZone;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, zone);
        var next = ScheduleCalculator.Next(schedule, nowLocal);
        if (next is null)
            return null;
        var local = DateTime.SpecifyKind(next.Value, DateTimeKind.Unspecified);
        // Die Stunde, die bei der Umstellung auf Sommerzeit ausfällt, gibt es nicht: auf die nächste gültige Zeit schieben.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private async Task StoreAsync(Schedule schedule, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Schedules.Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastRunUtc, schedule.LastRunUtc)
            .SetProperty(x => x.NextRunUtc, schedule.NextRunUtc)
            .SetProperty(x => x.LastResult, schedule.LastResult), ct);
    }
}
