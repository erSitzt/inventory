using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>Ergebnisse, die ein Agent nachliefert, weil der Server beim Ende des Jobs nicht lief.</summary>
public sealed class LateResultTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
    private readonly DeploymentService _deployments;
    private readonly ComputerService _computers;

    public LateResultTests()
    {
        _computers = new ComputerService(_db, new SimulatedHostProbe());
        var packages = new PackageService(_db, new PackageRepository(_repoDir));
        _deployments = new DeploymentService(_db, packages,
            new RemoteJobService([new SimulatedTransport(TransportKind.SmbService)], _computers, new CredentialService(_db, new ReversingProtector()), new SettingsService(_db)),
            new InventoryStore(_db), new SimulatedHostProbe());
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    /// <summary>Ein Ziel, wie es in der Datenbank steht, wenn der Server mitten im Job beendet wird.</summary>
    private async Task<(int ComputerId, int TargetId)> RunningTargetAsync(string jobId, Action<DeploymentTarget>? adjust = null)
    {
        var computer = (await _computers.ImportAsync("PC-" + jobId)).Single();
        await using var db = await ((IDbContextFactory<InventoryDbContext>)_db).CreateDbContextAsync();
        var target = new DeploymentTarget { ComputerId = computer.Id, ComputerName = computer.Name, Status = TargetStatus.Running, StartedUtc = DateTime.UtcNow, JobId = jobId };
        adjust?.Invoke(target);
        db.Deployments.Add(new Deployment { PackageName = "Demo", Targets = [target] });
        await db.SaveChangesAsync();
        return (computer.Id, target.Id);
    }

    private async Task<DeploymentTarget> TargetAsync(int id)
    {
        await using var db = await ((IDbContextFactory<InventoryDbContext>)_db).CreateDbContextAsync();
        return await db.DeploymentTargets.AsNoTracking().FirstAsync(t => t.Id == id);
    }

    private static JobResult Result(string jobId, bool success, params StepResult[] steps) =>
        new() { JobId = jobId, Success = success, Steps = [.. steps], BootTimeUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public async Task A_result_delivered_after_a_server_restart_replaces_the_provisional_failure_once()
    {
        var (computerId, targetId) = await RunningTargetAsync("job1");
        await _deployments.MarkInterruptedAsync();
        var interrupted = await TargetAsync(targetId);
        Assert.Equal((TargetStatus.Failed, true), (interrupted.Status, interrupted.Interrupted));

        var result = Result("job1", true, new StepResult { Index = 0, Name = "install", Status = StepStatus.Succeeded });
        Assert.True(await _deployments.AdoptResultAsync(computerId, "job1", result));

        var adopted = await TargetAsync(targetId);
        Assert.Equal((TargetStatus.Succeeded, false, null), (adopted.Status, adopted.Interrupted, adopted.Error));
        Assert.Equal("install", Assert.Single(adopted.Steps).Name);
        Assert.NotNull(adopted.FinishedUtc);
        // Ein zweites Mal (doppelte Zustellung) ändert nichts mehr.
        Assert.False(await _deployments.AdoptResultAsync(computerId, "job1", Result("job1", false)));
        Assert.Equal(TargetStatus.Succeeded, (await TargetAsync(targetId)).Status);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_result_are_adopted_exactly_once()
    {
        var (computerId, targetId) = await RunningTargetAsync("job8");
        await _deployments.MarkInterruptedAsync();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            _deployments.AdoptResultAsync(computerId, "job8", Result("job8", true, new StepResult { Index = 0, Name = "install", Status = StepStatus.Succeeded })))));
        Assert.Equal(1, outcomes.Count(adopted => adopted));
        Assert.Equal(TargetStatus.Succeeded, (await TargetAsync(targetId)).Status);
    }

    [Fact]
    public async Task Only_the_computer_and_job_of_an_interrupted_target_are_accepted()
    {
        var (computerId, targetId) = await RunningTargetAsync("job2");
        var (otherId, _) = await RunningTargetAsync("job3");
        // Noch nicht unterbrochen: der Job läuft für den Server noch, ein nachgeliefertes Ergebnis gibt es da nicht.
        Assert.False(await _deployments.AdoptResultAsync(computerId, "job2", Result("job2", true)));
        await _deployments.MarkInterruptedAsync();

        Assert.False(await _deployments.AdoptResultAsync(otherId, "job2", Result("job2", true)));
        Assert.False(await _deployments.AdoptResultAsync(computerId, "unknown", Result("unknown", true)));
        Assert.Equal(TargetStatus.Failed, (await TargetAsync(targetId)).Status);
    }

    [Fact]
    public async Task A_queued_target_is_not_waiting_for_a_result()
    {
        var (computerId, targetId) = await RunningTargetAsync("job4", t => t.Status = TargetStatus.Queued);
        await _deployments.MarkInterruptedAsync();
        Assert.False((await TargetAsync(targetId)).Interrupted);
        Assert.False(await _deployments.AdoptResultAsync(computerId, "job4", Result("job4", true)));
    }

    [Fact]
    public async Task A_late_failure_keeps_the_target_failed_with_the_real_error()
    {
        var (computerId, targetId) = await RunningTargetAsync("job5");
        await _deployments.MarkInterruptedAsync();
        Assert.True(await _deployments.AdoptResultAsync(computerId, "job5",
            Result("job5", false, new StepResult { Index = 0, Name = "install", Status = StepStatus.Failed, Error = "Exitcode 1603" })));
        var adopted = await TargetAsync(targetId);
        Assert.Equal((TargetStatus.Failed, "Exitcode 1603", false), (adopted.Status, adopted.Error, adopted.Interrupted));
    }

    [Fact]
    public async Task A_late_result_that_ends_with_a_restart_waits_for_the_remaining_steps()
    {
        var (computerId, targetId) = await RunningTargetAsync("job6");
        await _deployments.MarkInterruptedAsync();
        var result = Result("job6", true,
            new StepResult { Index = 0, Name = "install", Status = StepStatus.Succeeded },
            new StepResult { Index = 1, Name = "reboot", Status = StepStatus.Succeeded },
            new StepResult { Index = 2, Name = "configure", Status = StepStatus.Deferred });
        result.ResumeFromStep = 2;
        Assert.True(await _deployments.AdoptResultAsync(computerId, "job6", result));

        var adopted = await TargetAsync(targetId);
        Assert.Equal((TargetStatus.WaitingReboot, 2), (adopted.Status, adopted.ResumeFromStep));
        Assert.NotNull(adopted.RebootUtc);
        Assert.Null(adopted.FinishedUtc);
        Assert.Equal(result.BootTimeUtc, adopted.BootTimeUtc);
    }

    [Fact]
    public async Task A_late_result_of_a_continuation_keeps_the_steps_before_the_restart()
    {
        // Der unterbrochene Job war die Fortsetzung ab Schritt 2: die ersten beiden Ergebnisse stehen schon am Ziel.
        var (computerId, targetId) = await RunningTargetAsync("job7", t =>
        {
            t.ResumeFromStep = 2;
            t.Steps = [new StepResult { Index = 0, Name = "install", Status = StepStatus.Succeeded }, new StepResult { Index = 1, Name = "reboot", Status = StepStatus.Succeeded },
                new StepResult { Index = 2, Name = "configure", Status = StepStatus.Deferred }];
        });
        await _deployments.MarkInterruptedAsync();
        Assert.True(await _deployments.AdoptResultAsync(computerId, "job7", Result("job7", true, new StepResult { Index = 0, Name = "configure", Status = StepStatus.Succeeded })));

        var adopted = await TargetAsync(targetId);
        Assert.Equal(TargetStatus.Succeeded, adopted.Status);
        Assert.Equal([("install", 0), ("reboot", 1), ("configure", 2)], adopted.Steps.Select(s => (s.Name, s.Index)));
        Assert.Equal(StepStatus.Succeeded, adopted.Steps[2].Status);
        Assert.Null(adopted.ResumeFromStep);
    }
}
