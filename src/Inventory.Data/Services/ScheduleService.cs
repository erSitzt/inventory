using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

public sealed class ScheduleService(IDbContextFactory<InventoryDbContext> dbFactory, CollectionService collections, ScanService scans, DeploymentService deployments, TimeProvider time, IStringLocalizer<SharedResource>? localizer = null, Inventory.Core.Abstractions.IAlertSink? alerts = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


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
        schedule.RecheckDays = schedule.RecheckDays <= 0 ? 0 : Math.Clamp(schedule.RecheckDays, MinRecheckDays, 365);
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

        // Nur beim ersten Durchlauf nach dem Start kann ein Termin wegen eines ausgeschalteten Servers verpasst sein;
        // danach bedeutet Verspätung nur, dass ein früherer Lauf lange gedauert hat.
        var afterDowntime = !_checkedSinceStart;
        _checkedSinceStart = true;

        foreach (var schedule in due)
        {
            var late = now - schedule.NextRunUtc!.Value;
            var missed = afterDowntime && schedule.Trigger is ScheduleTrigger.Daily or ScheduleTrigger.Weekly or ScheduleTrigger.Monthly
                         && late >= (schedule.CatchUpMissed ? CatchUpLimit : MissedAfter);
            if (missed)
            {
                schedule.LastResult = L["Missed: the server was not running at {0}", TimeZoneInfo.ConvertTimeFromUtc(schedule.NextRunUtc.Value, time.LocalTimeZone).ToString("g")];
                schedule.NextRunUtc = NextUtc(schedule);
                await StoreAsync(schedule, ct);
                alerts?.Raise(new(AlertKind.ScheduleFailed, L["Schedule \"{0}\" missed", schedule.Name], schedule.LastResult, "schedules"));
                continue;
            }

            // Erst den nächsten Termin festschreiben: ein Absturz mitten im Lauf darf nicht zur Endlosschleife werden.
            schedule.LastRunUtc = now;
            schedule.NextRunUtc = NextUtc(schedule);
            // Die Pflicht-Verteilung läuft alle paar Minuten und tut meist nichts: der Zwischenstand wäre nur Flackern.
            if (schedule.Trigger != ScheduleTrigger.OnMembership)
            {
                schedule.LastResult = L["running"];
                await StoreAsync(schedule, ct);
            }

            schedule.LastResult = await RunAsync(schedule, ct, scheduled: true);
            await StoreAsync(schedule, ct);
        }
        return due.Count;
    }

    private bool _checkedSinceStart;
    private readonly SemaphoreSlim _membershipLock = new(1, 1);

    /// <summary>Eine erneute Ausführung der Pflicht-Verteilung gibt es höchstens wöchentlich.</summary>
    public const int MinRecheckDays = 7;

    /// <summary>Ein verpasster Lauf wird höchstens so lange nachgeholt.</summary>
    public static readonly TimeSpan CatchUpLimit = TimeSpan.FromHours(24);
    /// <summary>Ohne Nachholen gilt ein Lauf nach dieser Verspätung als verpasst.</summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromHours(1);
    /// <summary>Eine Pflicht-Verteilung startet je Durchlauf höchstens so viele Rechner und versucht einen fehlgeschlagenen erst nach dieser Frist erneut.</summary>
    public const int MembershipBatch = 50;
    public static readonly TimeSpan MembershipRetryAfter = TimeSpan.FromHours(24);

    /// <param name="Compliant">Mitglieder, auf denen das Paket über diesen Zeitplan erfolgreich war.</param>
    /// <param name="Members">Rechner im Ziel des Zeitplans.</param>
    public sealed record Compliance(int Compliant, int Members);

    /// <summary>Stand der Pflicht-Verteilungen, je Zeitplan.</summary>
    public async Task<Dictionary<int, Compliance>> ComplianceAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<int, Compliance>();
        foreach (var schedule in (await ListAsync(ct)).Where(s => s.Trigger == ScheduleTrigger.OnMembership))
        {
            var state = await MembershipStateAsync(schedule, ct);
            result[schedule.Id] = new Compliance(state.Compliant.Count, state.Members.Count);
        }
        return result;
    }

    private sealed record MembershipState(List<int> Members, HashSet<int> Compliant, List<int> Due);

    /// <summary>Wer zum Ziel gehört, wo das Paket schon erfolgreich war und wer jetzt an der Reihe ist.</summary>
    private async Task<MembershipState> MembershipStateAsync(Schedule schedule, CancellationToken ct)
    {
        var members = await TargetIdsAsync(schedule, ct);
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Nur Versuche mit dem Paket, das der Zeitplan jetzt verteilt: wird es ausgetauscht, beginnt die Zählung neu.
        // Je Rechner zählt der jüngste Versuch; nur dieser wird geladen, und nur für die aktuellen Mitglieder.
        var attempts = db.DeploymentTargets.AsNoTracking()
            .Join(db.Deployments.Where(d => d.ScheduleId == schedule.Id && d.PackageId == schedule.PackageId), t => t.DeploymentId, d => d.Id, (t, d) => t)
            .Where(t => t.ComputerId != null && members.Contains(t.ComputerId.Value));
        var latestIds = await attempts.GroupBy(t => t.ComputerId).Select(g => g.Max(t => t.Id)).ToListAsync(ct);
        var latest = (await db.DeploymentTargets.AsNoTracking().Where(t => latestIds.Contains(t.Id))
                .Select(t => new { ComputerId = t.ComputerId!.Value, t.Status, t.FinishedUtc }).ToListAsync(ct))
            .ToDictionary(t => t.ComputerId);
        var compliant = new HashSet<int>();
        var due = new List<int>();
        foreach (var id in members)
        {
            if (!latest.TryGetValue(id, out var last))
            {
                due.Add(id);
            }
            else if (last.Status.IsSuccess())
            {
                compliant.Add(id);
                if (schedule.RecheckDays > 0 && last.FinishedUtc is { } finished && now - finished >= TimeSpan.FromDays(schedule.RecheckDays))
                    due.Add(id);
            }
            else if (last.Status is TargetStatus.Failed or TargetStatus.Cancelled && (last.FinishedUtc is not { } failed || now - failed >= MembershipRetryAfter))
            {
                due.Add(id);
            }
            // Alles andere läuft oder wartet noch (offline, Neustart, Wartungsfenster).
        }
        return new MembershipState(members, compliant, due);
    }

    private async Task<List<int>> TargetIdsAsync(Schedule schedule, CancellationToken ct)
    {
        if (schedule.CollectionId is { } collectionId)
            return [.. await collections.GetMemberIdsAsync(collectionId, ct)];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Computers.Select(c => c.Id).ToListAsync(ct);
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

    /// <param name="scheduled">Regulärer Lauf: ein Fehler geht auch an die Alarmregeln. Beim Start von Hand sieht ihn der Mensch selbst.</param>
    private async Task<string> RunAsync(Schedule schedule, CancellationToken ct, bool scheduled = false)
    {
        try
        {
            if (schedule.Trigger == ScheduleTrigger.OnMembership)
                return await RunMembershipAsync(schedule, ct);

            var ids = await TargetIdsAsync(schedule, ct);
            if (ids.Count == 0)
                return L["No computers in the collection"];

            if (schedule.Action == ScheduleAction.Scan)
            {
                var outcomes = await scans.ScanAsync(ids, null, ct);
                var failed = outcomes.Count(o => !o.Success);
                return failed == 0 ? L["{0} computers scanned", outcomes.Count] : L["{0} scanned, {1} failed", outcomes.Count - failed, failed];
            }

            if (schedule.PackageId is not { } packageId)
                return L["No package selected"];
            var deployment = await deployments.StartAsync(packageId, ids, new DeploymentOptions(schedule.ScanAfter, schedule.RetryOffline, schedule.RespectMaintenanceWindow,
                schedule.MaxFailures, schedule.MaxFailurePercent, schedule.MaxParallel) { RequireApproved = true, ScheduleId = schedule.Id }, ct);
            return L["Deployment #{0} started on {1} computers", deployment.Id, deployment.Targets.Count];
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            if (scheduled)
                alerts?.Raise(new(AlertKind.ScheduleFailed, L["Schedule \"{0}\" failed", schedule.Name], ex.Message, "schedules"));
            return $"Fehler: {ex.Message}";
        }
    }

    /// <summary>Startet das Paket auf den Mitgliedern, die es über diesen Zeitplan noch nicht (oder nicht mehr aktuell genug) erhalten haben.</summary>
    private async Task<string> RunMembershipAsync(Schedule schedule, CancellationToken ct)
    {
        if (schedule.Action != ScheduleAction.Deploy || schedule.PackageId is not { } packageId)
            return L["No package selected"];
        // Prüfen und Starten gehören zusammen: sonst könnten der Takt und "Jetzt ausführen" dieselben Rechner zweimal beauftragen.
        await _membershipLock.WaitAsync(ct);
        try
        {
            var state = await MembershipStateAsync(schedule, ct);
            var status = L["{0} of {1} computers have the package", state.Compliant.Count, state.Members.Count].Value;
            if (state.Due.Count == 0)
                return status;
            var batch = state.Due.Take(MembershipBatch).ToList();
            var deployment = await deployments.StartAsync(packageId, batch, new DeploymentOptions(schedule.ScanAfter, schedule.RetryOffline, schedule.RespectMaintenanceWindow,
                schedule.MaxFailures, schedule.MaxFailurePercent, schedule.MaxParallel) { RequireApproved = true, ScheduleId = schedule.Id }, ct);
            return $"{status} – {L["Deployment #{0} started on {1} computers", deployment.Id, deployment.Targets.Count]}";
        }
        finally
        {
            _membershipLock.Release();
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
