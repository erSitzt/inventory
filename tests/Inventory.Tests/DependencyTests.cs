using Inventory.Contracts;
using Inventory.Data;
using Inventory.Data.Services;

namespace Inventory.Tests;

public sealed class DependencyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
    private readonly PackageService _packages;

    public DependencyTests() => _packages = new PackageService(_db, new PackageRepository(_repoDir));

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    private static JobStep Command(string name) => new() { Name = name, Type = StepType.Command, Script = "echo ok" };

    private async Task<int> PackageAsync(string name, string? detects, params string[] steps)
    {
        var package = await _packages.SaveAsync(null, name, null, null);
        foreach (var step in steps)
            await _packages.SaveStepAsync(package.Id, null, Command(step), null);
        if (detects is not null)
            await _packages.SaveDetectionAsync(package.Id, new StepConditions { SoftwareInstalled = detects }, verifyAfterInstall: true);
        return package.Id;
    }

    [Fact]
    public async Task Prerequisites_run_first_as_groups_with_their_own_detection()
    {
        var vc = await PackageAsync("VC Runtime", "Visual C++", "vc");
        var runtime = await PackageAsync("Runtime", "Runtime 8", "download", "install");
        var app = await PackageAsync("App", "App", "setup");
        await _packages.SetDependenciesAsync(runtime, [vc]);
        await _packages.SetDependenciesAsync(app, [runtime]);

        var built = await _packages.BuildJobAsync(app);

        Assert.Equal(["Runtime › VC Runtime › vc", "Runtime › download", "Runtime › install", "setup"], built.Manifest.Steps.Select(s => s.Name));
        Assert.Equal([2, 1, 1, null], built.Manifest.Steps.Select(s => s.Group));
        var outer = built.Manifest.Groups.Single(g => g.Id == 1);
        var inner = built.Manifest.Groups.Single(g => g.Id == 2);
        Assert.Equal(("Runtime", "Runtime 8", true, null), (outer.Name, outer.Detection!.SoftwareInstalled, outer.Verify, outer.Parent));
        Assert.Equal(("VC Runtime", 1), (inner.Name, inner.Parent));
        // Die Erkennung des verteilten Pakets bleibt die des Jobs.
        Assert.Equal("App", built.Manifest.Detection!.SoftwareInstalled);
        Assert.Equal(new[] { vc, runtime, app }.Order(), built.PackageIds.Order());
    }

    [Fact]
    public async Task A_prerequisite_cycle_is_rejected_and_changes_withdraw_the_approval()
    {
        var a = await PackageAsync("A", null, "a");
        var b = await PackageAsync("B", null, "b");
        await _packages.SetDependenciesAsync(a, [b]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _packages.SetDependenciesAsync(b, [a]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _packages.SetDependenciesAsync(a, [a]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _packages.SaveStepAsync(b, null, new JobStep { Name = "A" }, a));

        await _packages.ApproveAsync(a, "admin");
        await _packages.SetDependenciesAsync(a, [b]);
        Assert.True((await _packages.GetAsync(a))!.IsApproved);
        await _packages.SetDependenciesAsync(a, []);
        Assert.False((await _packages.GetAsync(a))!.IsApproved);
    }

    [Fact]
    public async Task Deleting_a_required_package_removes_the_prerequisite()
    {
        var a = await PackageAsync("A", null, "a");
        var b = await PackageAsync("B", null, "b");
        await _packages.SetDependenciesAsync(a, [b]);
        await _packages.ApproveAsync(a, "admin");
        await _packages.DeleteAsync(b);
        var left = (await _packages.GetAsync(a))!;
        Assert.Empty(left.Dependencies);
        // Ohne die Voraussetzung ist es nicht mehr das freigegebene Paket.
        Assert.False(left.IsApproved);
        Assert.Equal(["a"], (await _packages.BuildJobAsync(a)).Manifest.Steps.Select(s => s.Name));
    }

    private static JobManifest Manifest() => new()
    {
        Groups =
        [
            new JobGroup { Id = 1, Name = "Runtime", Detection = new StepConditions { SoftwareInstalled = "runtime" }, Verify = true },
            new JobGroup { Id = 2, Name = "VC", Detection = new StepConditions { SoftwareInstalled = "vc" }, Parent = 1 },
        ],
        Steps = [new JobStep { Name = "vc", Group = 2 }, new JobStep { Name = "r1", Group = 1 }, new JobStep { Name = "r2", Group = 1 }, new JobStep { Name = "app" }],
    };

    [Fact]
    public void A_group_is_detected_once_before_its_first_step()
    {
        var installed = new HashSet<string>();
        var asked = new List<string>();
        var manifest = Manifest();
        var groups = new JobGroups(manifest, d => { asked.Add(d.SoftwareInstalled!); return installed.Contains(d.SoftwareInstalled!) ? null : "missing"; });

        Assert.Null(groups.Present(manifest.Steps[0]));
        Assert.Null(groups.Present(manifest.Steps[1]));
        // Der erste Schritt hat die Voraussetzung schon erkennbar gemacht: der zweite läuft trotzdem.
        installed.Add("runtime");
        Assert.Null(groups.Present(manifest.Steps[2]));
        Assert.Null(groups.Present(manifest.Steps[3]));
        Assert.Equal(["runtime", "vc"], asked);
    }

    [Fact]
    public void A_present_outer_prerequisite_also_skips_the_inner_one()
    {
        var asked = new List<string>();
        var manifest = Manifest();
        var groups = new JobGroups(manifest, d => { asked.Add(d.SoftwareInstalled!); return d.SoftwareInstalled == "runtime" ? null : "missing"; });

        Assert.Equal("Runtime", groups.Present(manifest.Steps[0]));
        Assert.Equal("Runtime", groups.Present(manifest.Steps[2]));
        Assert.Null(groups.Present(manifest.Steps[3]));
        Assert.Equal(["runtime"], asked);
    }

    [Fact]
    public void A_group_without_detection_always_runs_and_an_inner_one_can_be_skipped_alone()
    {
        var manifest = Manifest();
        manifest.Groups[0].Detection = null;
        var groups = new JobGroups(manifest, d => d.SoftwareInstalled == "vc" ? null : "missing");

        Assert.Equal("VC", groups.Present(manifest.Steps[0]));
        Assert.Null(groups.Present(manifest.Steps[1]));
    }

    [Fact]
    public void After_a_restart_inside_a_prerequisite_its_remaining_steps_still_run()
    {
        var manifest = Manifest();
        Assert.Equal([1], JobGroups.Started(manifest, 2));
        Assert.Empty(JobGroups.Started(manifest, 3));
        Assert.Equal([1], JobGroups.Started(manifest, 1));
        Assert.Empty(JobGroups.Started(manifest, 0));

        manifest.StartedGroups = [1];
        manifest.Steps = [.. manifest.Steps.Skip(2)];
        var groups = new JobGroups(manifest, _ => null);
        Assert.Null(groups.Present(manifest.Steps[0]));
    }

    [Fact]
    public void A_prerequisite_that_ends_with_the_restart_is_verified_by_the_follow_up_job()
    {
        var manifest = Manifest();
        // Neustart als letzter Schritt der äußeren Voraussetzung (Index 2): der Folgejob beginnt beim Paket selbst.
        Assert.Equal([1], JobGroups.Finished(manifest, 3));
        Assert.Empty(JobGroups.Finished(manifest, 2));
        Assert.Equal([2], JobGroups.Finished(manifest, 1));

        Assert.Contains("Runtime", new JobGroups(manifest, _ => "not found").VerifyFinished([1]));
        Assert.Null(new JobGroups(manifest, _ => null).VerifyFinished([1]));
        // Die innere Gruppe verlangt keine Nachprüfung.
        Assert.Null(new JobGroups(manifest, _ => "not found").VerifyFinished([2]));
    }

    [Fact]
    public void A_prerequisite_must_be_detected_after_its_last_step()
    {
        var manifest = Manifest();
        var groups = new JobGroups(manifest, _ => "not found");

        // Die innere Gruppe prüft nicht nach, die äußere ist nach "r1" noch nicht zu Ende.
        Assert.Null(groups.VerifyFinished(manifest.Steps[0], manifest.Steps[1]));
        Assert.Null(groups.VerifyFinished(manifest.Steps[1], manifest.Steps[2]));
        Assert.Contains("Runtime", groups.VerifyFinished(manifest.Steps[2], manifest.Steps[3]));
        Assert.Null(groups.VerifyFinished(manifest.Steps[3], null));
        Assert.Null(new JobGroups(manifest, _ => null).VerifyFinished(manifest.Steps[2], manifest.Steps[3]));
    }
}
