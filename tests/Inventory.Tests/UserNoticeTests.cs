using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>Hinweis an den angemeldeten Benutzer mit der Möglichkeit, den Start zu verschieben.</summary>
public sealed class UserNoticeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
    private readonly PackageService _packages;
    private readonly ComputerService _computers;
    private readonly DeploymentService _deployments;

    public UserNoticeTests()
    {
        _packages = new PackageService(_db, new PackageRepository(_repoDir));
        _computers = new ComputerService(_db, new SimulatedHostProbe());
        _deployments = new DeploymentService(_db, _packages,
            new RemoteJobService([new SimulatedTransport(TransportKind.SmbService)], _computers, new CredentialService(_db, new ReversingProtector()), new SettingsService(_db)),
            new InventoryStore(_db), new SimulatedHostProbe());
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    private async Task<int> PackageAsync(bool askUser, int maxDeferrals = 2, int minutes = 30, string name = "Office")
    {
        var package = await _packages.SaveAsync(null, name, null, null);
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "setup", Type = StepType.Command, Script = "echo ok" }, null);
        await _packages.SaveNoticeAsync(package.Id, askUser, maxDeferrals, minutes);
        return package.Id;
    }

    private async Task<DeploymentTarget> WaitAsync(int deploymentId)
    {
        for (var i = 0; i < 200; i++)
        {
            if (!_deployments.IsRunning(deploymentId))
            {
                var target = (await _deployments.GetAsync(deploymentId))!.Targets.Single();
                if (target.Status is not (TargetStatus.Queued or TargetStatus.Running))
                    return target;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Deployment wurde nicht fertig");
    }

    /// <summary>Lässt die Frist des Benutzers ablaufen.</summary>
    private async Task ExpireAsync(int targetId)
    {
        await using var db = await ((IDbContextFactory<InventoryDbContext>)_db).CreateDbContextAsync();
        await db.DeploymentTargets.Where(t => t.Id == targetId).ExecuteUpdateAsync(s => s.SetProperty(t => t.NotBeforeUtc, DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task The_job_carries_a_question_only_when_the_package_asks_and_a_notice_when_postponing_is_used_up()
    {
        var silent = await _packages.BuildJobAsync(await PackageAsync(askUser: false, name: "Silent"));
        Assert.Null(silent.Notice);
        Assert.Null(_deployments.Prompt(silent.Notice, 0));

        var asking = (await _packages.BuildJobAsync(await PackageAsync(askUser: true, maxDeferrals: 2, minutes: 30))).Notice!;
        var first = _deployments.Prompt(asking, 0)!;
        Assert.True(first.CanDefer);
        Assert.Contains("Office", first.Text);
        Assert.Contains("30", first.Text);
        Assert.Equal(UserNotice.PromptSeconds, first.TimeoutSeconds);
        Assert.True(_deployments.Prompt(asking, 1)!.CanDefer);
        var last = _deployments.Prompt(asking, 2)!;
        Assert.False(last.CanDefer);
        Assert.Contains("Office", last.Text);
    }

    [Fact]
    public async Task Limits_of_the_notice_are_enforced_when_saving()
    {
        var id = await PackageAsync(askUser: true, maxDeferrals: 99, minutes: 1);
        var package = (await _packages.GetAsync(id))!;
        Assert.Equal((UserNotice.MaxDeferrals, UserNotice.MinMinutes), (package.MaxDeferrals, package.DeferMinutes));
        // Der Hinweis ändert nicht, was das Paket ausführt: eine Freigabe bleibt bestehen.
        await _packages.ApproveAsync(id, "admin");
        await _packages.SaveNoticeAsync(id, true, 3, 60);
        Assert.True((await _packages.GetAsync(id))!.IsApproved);
    }

    [Fact]
    public async Task A_user_can_postpone_until_the_allowance_is_used_up_and_then_the_package_runs()
    {
        var packageId = await PackageAsync(askUser: true, maxDeferrals: 2, minutes: 30);
        // Die Simulation verschiebt auf Rechnern mit "defer" im Namen, solange das Verschieben erlaubt ist.
        var computer = (await _computers.ImportAsync("PC-DEFER-1")).Single();
        var deployment = await _deployments.StartAsync(packageId, [computer.Id], new DeploymentOptions(ScanAfter: false));

        var target = await WaitAsync(deployment.Id);
        Assert.Equal((TargetStatus.WaitingUser, 1), (target.Status, target.Deferrals));
        Assert.True(target.NotBeforeUtc > DateTime.UtcNow.AddMinutes(25));
        Assert.Null(target.FinishedUtc);
        // Vor Ablauf der Frist geschieht nichts.
        Assert.Equal(0, await _deployments.ResumeDeferredAsync());

        await ExpireAsync(target.Id);
        Assert.Equal(1, await _deployments.ResumeDeferredAsync());
        target = await WaitAsync(deployment.Id);
        Assert.Equal((TargetStatus.WaitingUser, 2), (target.Status, target.Deferrals));

        // Aufgebraucht: jetzt ist es nur noch ein Hinweis, das Paket läuft.
        await ExpireAsync(target.Id);
        Assert.Equal(1, await _deployments.ResumeDeferredAsync());
        target = await WaitAsync(deployment.Id);
        Assert.Equal(TargetStatus.Succeeded, target.Status);
        Assert.Null(target.NotBeforeUtc);
        Assert.Equal("setup", Assert.Single(target.Steps).Name);
    }

    [Fact]
    public async Task Cancelling_a_deployment_also_ends_postponed_targets()
    {
        var packageId = await PackageAsync(askUser: true);
        var computer = (await _computers.ImportAsync("PC-DEFER-2")).Single();
        var deployment = await _deployments.StartAsync(packageId, [computer.Id], new DeploymentOptions(ScanAfter: false));
        Assert.Equal(TargetStatus.WaitingUser, (await WaitAsync(deployment.Id)).Status);

        await _deployments.CancelAsync(deployment.Id);
        Assert.Equal(TargetStatus.Cancelled, (await _deployments.GetAsync(deployment.Id))!.Targets.Single().Status);
        Assert.Equal(0, await _deployments.ResumeDeferredAsync());
    }

    [Fact]
    public async Task Without_the_notice_nobody_is_asked()
    {
        var packageId = await PackageAsync(askUser: false);
        var computer = (await _computers.ImportAsync("PC-DEFER-3")).Single();
        var deployment = await _deployments.StartAsync(packageId, [computer.Id], new DeploymentOptions(ScanAfter: false));
        Assert.Equal(TargetStatus.Succeeded, (await WaitAsync(deployment.Id)).Status);
    }
}
