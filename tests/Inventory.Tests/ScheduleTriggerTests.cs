using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class ScheduleTriggerTests
{
    private static Schedule Monthly(int day, int week = 2, DayOfWeek weekday = DayOfWeek.Tuesday, int offset = 0) => new()
    {
        Name = "m", Trigger = ScheduleTrigger.Monthly, MonthDay = day, MonthWeek = week, MonthWeekday = (int)weekday, MonthOffsetDays = offset, TimeOfDay = new TimeSpan(22, 0, 0),
    };

    [Fact]
    public void Monthly_runs_on_a_day_of_the_month_and_uses_the_last_day_in_shorter_months()
    {
        Assert.Equal(new DateTime(2026, 10, 15, 22, 0, 0), ScheduleCalculator.Next(Monthly(15), new DateTime(2026, 10, 7, 10, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 15, 22, 0, 0), ScheduleCalculator.Next(Monthly(15), new DateTime(2026, 10, 15, 22, 0, 0)));
        Assert.Equal(new DateTime(2027, 2, 28, 22, 0, 0), ScheduleCalculator.Next(Monthly(31), new DateTime(2027, 2, 1)));
        Assert.Equal(new DateTime(2028, 2, 29, 22, 0, 0), ScheduleCalculator.Next(Monthly(31), new DateTime(2028, 2, 1)));
        // Über den Jahreswechsel.
        Assert.Equal(new DateTime(2027, 1, 15, 22, 0, 0), ScheduleCalculator.Next(Monthly(15), new DateTime(2026, 12, 20)));
    }

    [Fact]
    public void Monthly_runs_on_the_nth_weekday_optionally_some_days_later()
    {
        // Zweiter Dienstag im Oktober 2026 ist der 13., im November der 10.
        Assert.Equal(new DateTime(2026, 10, 13, 22, 0, 0), ScheduleCalculator.Next(Monthly(0), new DateTime(2026, 10, 7)));
        Assert.Equal(new DateTime(2026, 11, 10, 22, 0, 0), ScheduleCalculator.Next(Monthly(0), new DateTime(2026, 10, 14)));
        // Drei Tage nach dem Patchday.
        Assert.Equal(new DateTime(2026, 10, 16, 22, 0, 0), ScheduleCalculator.Next(Monthly(0, offset: 3), new DateTime(2026, 10, 14)));
        // Letzter Freitag: Oktober 2026 hat fünf Freitage (30.), November nur vier (27.).
        Assert.Equal(new DateTime(2026, 10, 30, 22, 0, 0), ScheduleCalculator.Next(Monthly(0, 5, DayOfWeek.Friday), new DateTime(2026, 10, 7)));
        Assert.Equal(new DateTime(2026, 11, 27, 22, 0, 0), ScheduleCalculator.Next(Monthly(0, 5, DayOfWeek.Friday), new DateTime(2026, 10, 31)));
        // Mit Versatz in den Folgemonat: letzter Freitag im Oktober + 5 Tage = 4. November, von dort aus gesehen noch offen.
        Assert.Equal(new DateTime(2026, 11, 4, 22, 0, 0), ScheduleCalculator.Next(Monthly(0, 5, DayOfWeek.Friday, 5), new DateTime(2026, 11, 1)));
    }

    [Fact]
    public void A_schedule_stops_after_its_end_date()
    {
        var daily = new Schedule { Name = "d", Trigger = ScheduleTrigger.Daily, TimeOfDay = new TimeSpan(12, 0, 0), EndLocal = new DateTime(2026, 10, 8, 23, 59, 59) };
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0), ScheduleCalculator.Next(daily, new DateTime(2026, 10, 7, 13, 0, 0)));
        Assert.Null(ScheduleCalculator.Next(daily, new DateTime(2026, 10, 8, 13, 0, 0)));
    }

    private sealed class Rig : IDisposable
    {
        public TestDb Db { get; } = new();
        public Clock Time { get; }
        public ComputerService Computers { get; }
        public CollectionService Collections { get; }
        public PackageService Packages { get; }
        public DeploymentService Deployments { get; }
        private readonly string _repo = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
        private readonly IRemoteTransport _transport;

        /// <param name="realTime">Die Uhr beginnt jetzt statt an einem festen Tag: nötig, wo sie mit den Zeitstempeln der Deployments verglichen wird.</param>
        public Rig(IRemoteTransport? transport = null, bool realTime = false)
        {
            Time = new Clock(realTime ? DateTimeOffset.UtcNow : new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
            _transport = transport ?? new SimulatedTransport(TransportKind.SmbService);
            Computers = new ComputerService(Db, new SimulatedHostProbe());
            Collections = new CollectionService(Db);
            Packages = new PackageService(Db, new PackageRepository(_repo));
            Deployments = new DeploymentService(Db, Packages, new RemoteJobService([_transport], Computers, new CredentialService(Db, new ReversingProtector()), new SettingsService(Db)),
                new InventoryStore(Db), new SimulatedHostProbe());
        }

        /// <summary>Ein neuer Dienst steht für einen neu gestarteten Server.</summary>
        public ScheduleService Schedules() => new(Db, Collections, new ScanService(null!, new InventoryStore(Db)), Deployments, Time);

        public async Task WaitAsync()
        {
            for (var i = 0; i < 400; i++)
            {
                var open = (await Deployments.ListAsync()).Where(d => d.FinishedUtc is null || Deployments.IsRunning(d.Id)).ToList();
                if (open.Count == 0)
                    return;
                await Task.Delay(50);
            }
            throw new TimeoutException("Deployments wurden nicht fertig");
        }

        public void Dispose()
        {
            Db.Dispose();
            Directory.Delete(_repo, true);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task A_run_missed_while_the_server_was_off_is_caught_up_within_a_day_or_skipped()
    {
        using var rig = new Rig();
        var package = await rig.Packages.SaveAsync(null, "Tool", "1", null);
        await rig.Packages.SaveStepAsync(package.Id, null, new JobStep { Name = "x", Type = StepType.Command, Script = "echo" }, null);
        await rig.Packages.ApproveAsync(package.Id, "admin");
        await rig.Computers.ImportAsync("pc-01");
        Schedule Daily(string name, bool catchUp) => new()
        {
            Name = name, Action = ScheduleAction.Deploy, PackageId = package.Id, Trigger = ScheduleTrigger.Daily, TimeOfDay = new TimeSpan(12, 0, 0), CatchUpMissed = catchUp,
        };
        var first = rig.Schedules();
        await first.SaveAsync(Daily("holt nach", true));
        await first.SaveAsync(Daily("lässt aus", false));

        // Der Server war zum Termin aus und startet fünf Stunden danach.
        rig.Time.Advance(TimeSpan.FromHours(8));
        await rig.Schedules().RunDueAsync();
        await rig.WaitAsync();
        var stored = (await first.ListAsync()).ToDictionary(s => s.Name);
        Assert.StartsWith("Deployment #", stored["holt nach"].LastResult);
        Assert.StartsWith("Missed", stored["lässt aus"].LastResult);
        Assert.Null(stored["lässt aus"].LastRunUtc);
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0), stored["lässt aus"].NextRunUtc);

        // Mehr als einen Tag aus: auch das Nachholen entfällt.
        rig.Time.Advance(TimeSpan.FromHours(50));
        await rig.Schedules().RunDueAsync();
        stored = (await first.ListAsync()).ToDictionary(s => s.Name);
        Assert.StartsWith("Missed", stored["holt nach"].LastResult);

        // Läuft der Server durch, ist Verspätung kein Ausfall: derselbe Dienst führt auch spät noch aus.
        var running = rig.Schedules();
        await running.RunDueAsync();
        rig.Time.Advance(TimeSpan.FromHours(30));
        Assert.Equal(2, await running.RunDueAsync());
        await rig.WaitAsync();
        stored = (await first.ListAsync()).ToDictionary(s => s.Name);
        Assert.StartsWith("Deployment #", stored["lässt aus"].LastResult);
    }

    [Fact]
    public async Task A_required_deployment_reaches_every_member_once_including_later_ones()
    {
        using var rig = new Rig();
        var schedules = rig.Schedules();
        var package = await rig.Packages.SaveAsync(null, "Baseline", "1", null);
        await rig.Packages.SaveStepAsync(package.Id, null, new JobStep { Name = "x", Type = StepType.Command, Script = "echo" }, null);
        await rig.Packages.ApproveAsync(package.Id, "admin");
        var ids = (await rig.Computers.ImportAsync("lap-01 lap-02 pc-01")).ToDictionary(c => c.Name, c => c.Id);
        var laptops = await rig.Collections.SaveAsync(null, "Laptops", null, true, true, [new CollectionRule { Field = RuleField.ComputerName, Operator = RuleOperator.StartsWith, Value = "lap" }]);
        await schedules.SaveAsync(new Schedule
        {
            Name = "Pflicht", Action = ScheduleAction.Deploy, PackageId = package.Id, CollectionId = laptops.Id, Trigger = ScheduleTrigger.OnMembership, RetryOffline = false,
        });

        // Erster Durchlauf: beide Laptops, nicht der PC.
        Assert.Equal(1, await schedules.RunDueAsync());
        await rig.WaitAsync();
        var deployment = Assert.Single(await rig.Deployments.ListAsync());
        Assert.Equal(["lap-01", "lap-02"], deployment.Targets.Select(t => t.ComputerName).Order());
        Assert.NotNull(deployment.ScheduleId);
        Assert.Equal(new ScheduleService.Compliance(2, 2), (await schedules.ComplianceAsync()).Values.Single());

        // Nichts zu tun: kein neues Deployment, nur der Stand.
        rig.Time.Advance(TimeSpan.FromMinutes(ScheduleCalculator.MembershipCheckMinutes + 1));
        Assert.Equal(1, await schedules.RunDueAsync());
        Assert.Single(await rig.Deployments.ListAsync());
        Assert.Equal("2 of 2 computers have the package", (await schedules.ListAsync()).Single().LastResult);

        // Ein neuer Laptop kommt dazu und wird als einziger versorgt.
        await rig.Computers.ImportAsync("lap-03");
        rig.Time.Advance(TimeSpan.FromMinutes(ScheduleCalculator.MembershipCheckMinutes + 1));
        await schedules.RunDueAsync();
        await rig.WaitAsync();
        var all = await rig.Deployments.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("lap-03", Assert.Single(all.MaxBy(d => d.Id)!.Targets).ComputerName);
        Assert.Equal(new ScheduleService.Compliance(3, 3), (await schedules.ComplianceAsync()).Values.Single());

        // Ein Deployment von Hand zählt nicht für den Zeitplan, und umgekehrt stört es ihn nicht.
        await rig.Deployments.StartAsync(package.Id, [ids["pc-01"]], new DeploymentOptions(false));
        await rig.WaitAsync();
        Assert.Equal(new ScheduleService.Compliance(3, 3), (await schedules.ComplianceAsync()).Values.Single());
    }

    [Fact]
    public async Task A_required_deployment_retries_failures_after_a_day_and_rechecks_on_request()
    {
        var transport = new SwitchableTransport();
        using var rig = new Rig(transport, realTime: true);
        var schedules = rig.Schedules();
        var package = await rig.Packages.SaveAsync(null, "Baseline", "1", null);
        await rig.Packages.SaveStepAsync(package.Id, null, new JobStep { Name = "x", Type = StepType.Command, Script = "echo" }, null);
        await rig.Packages.ApproveAsync(package.Id, "admin");
        await rig.Computers.ImportAsync("pc-01");
        var schedule = new Schedule { Name = "Pflicht", Action = ScheduleAction.Deploy, PackageId = package.Id, Trigger = ScheduleTrigger.OnMembership, RetryOffline = false, MaxFailurePercent = null };
        await schedules.SaveAsync(schedule);

        transport.Fail = true;
        await schedules.RunDueAsync();
        await rig.WaitAsync();
        Assert.Equal(new ScheduleService.Compliance(0, 1), (await schedules.ComplianceAsync()).Values.Single());

        // Kurz danach: der Fehlschlag wird nicht alle paar Minuten wiederholt.
        rig.Time.Advance(TimeSpan.FromHours(1));
        await schedules.RunDueAsync();
        Assert.Single(await rig.Deployments.ListAsync());

        // Nach einem Tag der nächste Versuch, diesmal mit Erfolg.
        transport.Fail = false;
        rig.Time.Advance(TimeSpan.FromHours(24));
        await schedules.RunDueAsync();
        await rig.WaitAsync();
        Assert.Equal(2, (await rig.Deployments.ListAsync()).Count);
        Assert.Equal(new ScheduleService.Compliance(1, 1), (await schedules.ComplianceAsync()).Values.Single());

        // Ohne erneute Prüfung bleibt es dabei, mit ihr läuft das Paket nach der Frist noch einmal.
        rig.Time.Advance(TimeSpan.FromDays(8));
        await schedules.RunDueAsync();
        Assert.Equal(2, (await rig.Deployments.ListAsync()).Count);
        var stored = (await schedules.ListAsync()).Single();
        stored.RecheckDays = 7;
        await schedules.SaveAsync(stored);
        rig.Time.Advance(TimeSpan.FromMinutes(ScheduleCalculator.MembershipCheckMinutes + 1));
        await schedules.RunDueAsync();
        await rig.WaitAsync();
        Assert.Equal(3, (await rig.Deployments.ListAsync()).Count);
    }

    /// <summary>Simulierter Transport, dessen Jobs sich auf Fehlschlag stellen lassen.</summary>
    private sealed class SwitchableTransport : IRemoteTransport
    {
        private readonly SimulatedTransport _inner = new(TransportKind.SmbService);
        public bool Fail { get; set; }
        public TransportKind Kind => _inner.Kind;

        public async Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            if (Fail)
                return JobResult.Failed(job.JobId, "simulated failure");
            return await _inner.RunAsync(target, job, files, progress, ct);
        }
    }
}
