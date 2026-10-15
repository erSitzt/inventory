using Inventory.Contracts;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>Der Agent setzt nach einem Neustart selbst fort; der Server wartet auf sein Ergebnis statt einen Folgejob zu starten.</summary>
public sealed class LocalResumeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
    private readonly DeploymentService _deployments;
    private readonly ComputerService _computers;
    private static readonly DateTime FirstBoot = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    public LocalResumeTests()
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

    private async Task<(int ComputerId, int TargetId)> TargetAsync(string jobId, Action<DeploymentTarget> adjust)
    {
        var computer = (await _computers.ImportAsync("PC-" + jobId)).Single();
        await using var db = await ((IDbContextFactory<InventoryDbContext>)_db).CreateDbContextAsync();
        var target = new DeploymentTarget { ComputerId = computer.Id, ComputerName = computer.Name, StartedUtc = DateTime.UtcNow, JobId = jobId };
        adjust(target);
        db.Deployments.Add(new Deployment { PackageName = "Demo", Targets = [target] });
        await db.SaveChangesAsync();
        return (computer.Id, target.Id);
    }

    private async Task<DeploymentTarget> LoadAsync(int id)
    {
        await using var db = await ((IDbContextFactory<InventoryDbContext>)_db).CreateDbContextAsync();
        return await db.DeploymentTargets.AsNoTracking().FirstAsync(t => t.Id == id);
    }

    private static StepResult Step(int index, string name, StepStatus status = StepStatus.Succeeded) => new() { Index = index, Name = name, Status = status };

    /// <summary>Der erste Teil bis zum Neustart, wie ihn der Agent meldet: Neustart erfolgreich, Rest folgt beim Agent.</summary>
    private static JobResult FirstPart(string jobId) => new()
    {
        JobId = jobId, Success = true, ResumeFromStep = 2, ResumesLocally = true, BootTimeUtc = FirstBoot,
        Steps = [Step(0, "install"), Step(1, "reboot"), Step(2, "configure", StepStatus.Deferred)],
    };

    [Fact]
    public async Task The_server_waits_for_the_agent_and_merges_its_continuation()
    {
        // Der Server lief beim ersten Teil nicht mit (z. B. Agent offline): das Ergebnis kommt nachgeliefert.
        var (computerId, targetId) = await TargetAsync("loc1", t => { t.Status = TargetStatus.Failed; t.Interrupted = true; });
        Assert.True(await _deployments.AdoptResultAsync(computerId, "loc1", FirstPart("loc1")));

        var waiting = await LoadAsync(targetId);
        Assert.Equal((TargetStatus.WaitingReboot, true, 2), (waiting.Status, waiting.ResumesLocally, waiting.ResumeFromStep));
        Assert.Contains("agent", waiting.Error);

        // Kein Folgejob vom Server, auch wenn der Rechner erreichbar ist.
        Assert.Equal(0, await _deployments.ResumeRebootedAsync());
        Assert.Equal(TargetStatus.WaitingReboot, (await LoadAsync(targetId)).Status);

        // Die Fortsetzung des Agents: Schrittnummern beginnen wieder bei 0, wie bei einem Folgejob des Servers.
        var continuation = new JobResult { JobId = "loc1", Success = true, BootTimeUtc = FirstBoot.AddHours(1), Steps = [Step(0, "configure")] };
        Assert.True(await _deployments.AdoptResultAsync(computerId, "loc1", continuation));

        var done = await LoadAsync(targetId);
        Assert.Equal((TargetStatus.Succeeded, false), (done.Status, done.ResumesLocally));
        Assert.Equal(["install", "reboot", "configure"], done.Steps.Select(s => s.Name));
        Assert.Equal([0, 1, 2], done.Steps.Select(s => s.Index));
        // Doppelt zugestellt: nichts mehr zu übernehmen.
        Assert.False(await _deployments.AdoptResultAsync(computerId, "loc1", continuation));
    }

    [Fact]
    public async Task A_second_restart_in_the_continuation_keeps_waiting_for_the_agent()
    {
        var (computerId, targetId) = await TargetAsync("loc2", t => { t.Status = TargetStatus.Failed; t.Interrupted = true; });
        await _deployments.AdoptResultAsync(computerId, "loc2", FirstPart("loc2"));

        var second = new JobResult
        {
            JobId = "loc2", Success = true, ResumeFromStep = 1, ResumesLocally = true, BootTimeUtc = FirstBoot.AddHours(1),
            Steps = [Step(0, "reboot again"), Step(1, "finish", StepStatus.Deferred)],
        };
        Assert.True(await _deployments.AdoptResultAsync(computerId, "loc2", second));
        var waiting = await LoadAsync(targetId);
        Assert.Equal((TargetStatus.WaitingReboot, true, 3), (waiting.Status, waiting.ResumesLocally, waiting.ResumeFromStep));

        Assert.True(await _deployments.AdoptResultAsync(computerId, "loc2", new JobResult { JobId = "loc2", Success = true, Steps = [Step(0, "finish")] }));
        var done = await LoadAsync(targetId);
        Assert.Equal(TargetStatus.Succeeded, done.Status);
        Assert.Equal(["install", "reboot", "reboot again", "finish"], done.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task A_cancelled_target_rejects_the_continuation()
    {
        var (computerId, targetId) = await TargetAsync("loc3", t => { t.Status = TargetStatus.Failed; t.Interrupted = true; });
        await _deployments.AdoptResultAsync(computerId, "loc3", FirstPart("loc3"));
        await _deployments.CancelAsync((await LoadAsync(targetId)).DeploymentId);

        Assert.False(await _deployments.AdoptResultAsync(computerId, "loc3", new JobResult { JobId = "loc3", Success = true, Steps = [Step(0, "configure")] }));
        Assert.Equal(TargetStatus.Cancelled, (await LoadAsync(targetId)).Status);
    }

    [Fact]
    public async Task Without_a_report_the_target_fails_after_the_agent_would_have_given_up()
    {
        var (_, targetId) = await TargetAsync("loc4", t =>
        {
            t.Status = TargetStatus.WaitingReboot; t.ResumesLocally = true; t.ResumeFromStep = 2;
            t.RebootUtc = DateTime.UtcNow - DeploymentService.LocalResumeLimit - TimeSpan.FromHours(1);
        });
        await _deployments.ResumeRebootedAsync();

        var failed = await LoadAsync(targetId);
        Assert.Equal((TargetStatus.Failed, false), (failed.Status, failed.ResumesLocally));
        Assert.NotNull(failed.FinishedUtc);
    }

    [Fact]
    public void The_continuation_manifest_keeps_the_job_and_skips_neither_detection_nor_prompt_checks()
    {
        var manifest = new JobManifest
        {
            JobId = "loc5", ExpectedComputerName = "PC-1", SkipIfDetected = true, VerifyAfterInstall = true,
            Detection = new StepConditions { SoftwareInstalled = "Demo" },
            UserPrompt = new UserPrompt { Title = "t", Text = "x", CanDefer = true },
            Steps = [new JobStep { Name = "a" }, new JobStep { Name = "reboot", Type = StepType.Reboot }, new JobStep { Name = "b" }],
        };

        var next = manifest.ContinueFrom(2, FirstBoot);

        Assert.Equal(("loc5", "PC-1", FirstBoot), (next.JobId, next.ExpectedComputerName, next.RequireBootAfterUtc));
        Assert.Equal(["b"], next.Steps.Select(s => s.Name));
        Assert.False(next.SkipIfDetected);
        Assert.True(next.VerifyAfterInstall);
        Assert.Null(next.UserPrompt);
    }
}
