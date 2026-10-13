using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public sealed class PhasedRolloutTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offline = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComputerService _computers;
    private readonly PackageService _packages;
    private readonly CollectionService _collections;
    private readonly DeploymentService _deployments;

    public PhasedRolloutTests()
    {
        _computers = new ComputerService(_db, new SimulatedHostProbe());
        _packages = new PackageService(_db, new PackageRepository(_repoDir));
        _collections = new CollectionService(_db);
        _deployments = new DeploymentService(_db, _packages,
            new RemoteJobService([new SelectiveTransport(_failing)], _computers, new CredentialService(_db, new ReversingProtector()), new SettingsService(_db)),
            new InventoryStore(_db), new FixedProbe(host => new ProbeResult(!_offline.Contains(host), "10.0.0.1")));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    /// <summary>Jobs gelingen, außer auf den benannten Rechnern.</summary>
    private sealed class SelectiveTransport(HashSet<string> failing) : IRemoteTransport
    {
        public TransportKind Kind => TransportKind.SmbService;

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            var fails = failing.Contains(target.Host);
            return Task.FromResult(new JobResult
            {
                JobId = job.JobId, Success = !fails, Error = fails ? "Exitcode 1" : null,
                Steps = [.. job.Steps.Select((s, i) => new StepResult { Index = i, Name = s.Name, Status = fails ? StepStatus.Failed : StepStatus.Succeeded })],
            });
        }
    }

    /// <summary>Acht Rechner, die ersten <paramref name="pilots"/> davon in der Pilot-Sammlung.</summary>
    private async Task<(int Package, List<Computer> Computers, int Pilot)> SetupAsync(int pilots = 2)
    {
        var package = await _packages.SaveAsync(null, "Setup", null, null);
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Setup", Type = StepType.Command, Script = "echo ok" }, null);
        var computers = (await _computers.ImportAsync("pc-01 pc-02 pc-03 pc-04 pc-05 pc-06 pc-07 pc-08")).OrderBy(c => c.Name).ToList();
        var collection = await _collections.SaveAsync(null, "Pilot", null, false, true, []);
        await _collections.AddMembersAsync(collection.Id, computers.Take(pilots).Select(c => c.Id));
        return (package.Id, computers, collection.Id);
    }

    private async Task<Deployment> WaitAsync(int id)
    {
        for (var i = 0; i < 200; i++)
        {
            var deployment = await _deployments.GetAsync(id);
            if (deployment!.FinishedUtc is not null && !_deployments.IsRunning(id))
                return deployment;
            await Task.Delay(50);
        }
        throw new TimeoutException("Deployment wurde nicht fertig");
    }

    private async Task<Deployment> StartAsync(int package, List<Computer> computers, int pilot, int holdHours = 0, int maxFailurePercent = 0) =>
        await WaitAsync((await _deployments.StartAsync(package, computers.Select(c => c.Id),
            new DeploymentOptions(false) { PilotCollectionId = pilot, PilotHoldHours = holdHours, PilotMaxFailurePercent = maxFailurePercent })).Id);

    /// <summary>Lässt die Pilotgruppe vor so vielen Stunden fertig geworden sein.</summary>
    private async Task AgeAsync(int deploymentId, int hours)
    {
        await using var db = _db.CreateDbContext();
        foreach (var target in db.DeploymentTargets.Where(t => t.DeploymentId == deploymentId && t.Pilot))
            target.FinishedUtc = DateTime.UtcNow.AddHours(-hours);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Only_the_pilot_group_runs_first_and_the_others_follow_after_the_wait()
    {
        var (package, computers, pilot) = await SetupAsync();

        var first = await StartAsync(package, computers, pilot, holdHours: 24);

        Assert.Equal(["pc-01", "pc-02"], first.Targets.Where(t => t.Pilot).Select(t => t.ComputerName));
        Assert.All(first.Targets.Where(t => t.Pilot), t => Assert.Equal(TargetStatus.Succeeded, t.Status));
        Assert.All(first.Targets.Where(t => !t.Pilot), t => Assert.Equal(TargetStatus.WaitingPilot, t.Status));
        var state = DeploymentService.Pilot(first)!;
        Assert.Equal((2, 0, 0), (state.Total, state.Failed, state.Pending));
        Assert.NotNull(state.ReleaseUtc);

        // Vor Ablauf der Wartezeit geschieht nichts.
        Assert.Equal(0, await _deployments.ReleasePilotsAsync());
        await AgeAsync(first.Id, 25);
        Assert.Equal(6, await _deployments.ReleasePilotsAsync());

        var done = await WaitAsync(first.Id);
        Assert.All(done.Targets, t => Assert.Equal(TargetStatus.Succeeded, t.Status));
        Assert.NotNull(done.PilotReleasedUtc);
        Assert.Null(DeploymentService.Pilot(done));
        // Freigegeben wird nur einmal.
        Assert.Equal(0, await _deployments.ReleasePilotsAsync());
    }

    [Fact]
    public async Task Too_many_failures_in_the_pilot_group_stop_the_deployment_at_once()
    {
        var (package, computers, pilot) = await SetupAsync();
        _failing.Add("pc-01");

        var first = await StartAsync(package, computers, pilot, holdHours: 24, maxFailurePercent: 25);
        // Die Wartezeit gilt nur für die Freigabe: ein Fehlschlag muss nicht abgewartet werden.
        Assert.Equal(0, await _deployments.ReleasePilotsAsync());

        var stopped = (await _deployments.GetAsync(first.Id))!;
        Assert.Contains("1 of 2", stopped.StopReason);
        Assert.All(stopped.Targets.Where(t => !t.Pilot), t => Assert.Equal(TargetStatus.Cancelled, t.Status));
        Assert.All(stopped.Targets.Where(t => !t.Pilot), t => Assert.Equal(stopped.StopReason, t.Error));
        Assert.Null(DeploymentService.Pilot(stopped));
    }

    [Fact]
    public async Task Failures_within_the_allowed_rate_do_not_stop_it()
    {
        var (package, computers, pilot) = await SetupAsync(pilots: 4);
        _failing.Add("pc-01");

        var first = await StartAsync(package, computers, pilot, maxFailurePercent: 25);

        Assert.Equal(4, await _deployments.ReleasePilotsAsync());
        var done = await WaitAsync(first.Id);
        Assert.Equal(7, done.Targets.Count(t => t.Status == TargetStatus.Succeeded));
        Assert.Null(done.StopReason);
    }

    [Fact]
    public async Task The_wait_starts_only_when_the_whole_pilot_group_is_finished()
    {
        var (package, computers, pilot) = await SetupAsync();
        _offline.Add("pc-02");

        var first = await WaitAsync((await _deployments.StartAsync(package, computers.Select(c => c.Id),
            new DeploymentOptions(false, RetryOffline: true) { PilotCollectionId = pilot, PilotHoldHours = 0 })).Id);

        Assert.Equal(TargetStatus.WaitingOffline, first.Targets.Single(t => t.ComputerName == "pc-02").Status);
        var state = DeploymentService.Pilot(first)!;
        Assert.Equal(1, state.Pending);
        Assert.Null(state.ReleaseUtc);
        Assert.Equal(0, await _deployments.ReleasePilotsAsync());

        _offline.Clear();
        Assert.Equal(1, await _deployments.RetryWaitingAsync());
        await WaitAsync(first.Id);
        Assert.Equal(6, await _deployments.ReleasePilotsAsync());
        Assert.All((await WaitAsync(first.Id)).Targets, t => Assert.Equal(TargetStatus.Succeeded, t.Status));
    }

    [Fact]
    public async Task A_person_can_release_the_others_despite_wait_and_failures()
    {
        var (package, computers, pilot) = await SetupAsync();
        _failing.Add("pc-01");
        _failing.Add("pc-02");
        var first = await StartAsync(package, computers, pilot, holdHours: 48);

        Assert.Equal(6, await _deployments.ReleasePilotAsync(first.Id, force: true));

        var done = await WaitAsync(first.Id);
        Assert.Equal(6, done.Targets.Count(t => t.Status == TargetStatus.Succeeded));
        Assert.Equal(0, await _deployments.ReleasePilotAsync(first.Id, force: true));
    }

    [Fact]
    public async Task Cancelling_drops_the_computers_that_wait_for_the_pilot_group()
    {
        var (package, computers, pilot) = await SetupAsync();
        var first = await StartAsync(package, computers, pilot, holdHours: 24);

        await _deployments.CancelAsync(first.Id);

        var cancelled = (await _deployments.GetAsync(first.Id))!;
        Assert.All(cancelled.Targets.Where(t => !t.Pilot), t => Assert.Equal(TargetStatus.Cancelled, t.Status));
        Assert.Equal(0, await _deployments.ReleasePilotsAsync());
    }

    [Fact]
    public async Task Without_pilot_members_among_the_targets_everything_starts_at_once()
    {
        var (package, computers, pilot) = await SetupAsync();

        // Keiner der Rechner gehört zur Pilotgruppe, bzw. alle: es gibt nichts zu stufen.
        var none = await StartAsync(package, [.. computers.Skip(2)], pilot);
        var all = await StartAsync(package, [.. computers.Take(2)], pilot);

        foreach (var deployment in new[] { none, all })
        {
            Assert.Null(deployment.PilotCollectionId);
            Assert.All(deployment.Targets, t => Assert.Equal(TargetStatus.Succeeded, t.Status));
            Assert.DoesNotContain(deployment.Targets, t => t.Pilot);
        }
    }

    [Fact]
    public async Task A_deleted_pilot_collection_refuses_the_start_instead_of_deploying_to_everyone()
    {
        var (package, computers, pilot) = await SetupAsync();
        await _collections.DeleteAsync(pilot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _deployments.StartAsync(package, computers.Select(c => c.Id),
            new DeploymentOptions(false) { PilotCollectionId = pilot }));
        await using var db = _db.CreateDbContext();
        Assert.Empty(db.Deployments);
    }

    [Fact]
    public async Task A_schedule_passes_its_pilot_group_on()
    {
        var (package, computers, pilot) = await SetupAsync();
        await _packages.ApproveAsync(package, "test");
        var schedules = new ScheduleService(_db, _collections, null!, _deployments, TimeProvider.System);
        var schedule = new Schedule
        {
            Name = "Gestuft", Action = ScheduleAction.Deploy, PackageId = package, Trigger = ScheduleTrigger.Once, StartLocal = DateTime.Now.AddDays(1),
            RetryOffline = false, PilotCollectionId = pilot, PilotHoldHours = 12, PilotMaxFailurePercent = 50,
        };
        await schedules.SaveAsync(schedule);

        var result = await schedules.RunNowAsync(schedule.Id);

        Assert.Contains("pilot group", result);
        await using var db = _db.CreateDbContext();
        var deployment = db.Deployments.Single();
        Assert.Equal((pilot, 12, 50), (deployment.PilotCollectionId, deployment.PilotHoldHours, deployment.PilotMaxFailurePercent));
    }
}
