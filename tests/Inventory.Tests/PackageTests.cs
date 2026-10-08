using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public sealed class PackageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
    private readonly PackageService _packages;
    private readonly ComputerService _computers;

    public PackageTests()
    {
        _packages = new PackageService(_db, new PackageRepository(_repoDir));
        _computers = new ComputerService(_db, new SimulatedHostProbe());
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    private DeploymentService CreateDeployments(IRemoteTransport transport) =>
        new(_db, _packages, new RemoteJobService([transport], _computers, new CredentialService(_db, new ReversingProtector()), new SettingsService(_db)), new InventoryStore(_db));

    private static JobStep Command(string name, string script = "echo ok") => new() { Name = name, Type = StepType.Command, Script = script };

    private static async Task<Deployment> WaitAsync(DeploymentService deployments, int id)
    {
        for (var i = 0; i < 200; i++)
        {
            var deployment = await deployments.GetAsync(id);
            if (deployment!.FinishedUtc is not null && !deployments.IsRunning(id))
                return deployment;
            await Task.Delay(50);
        }
        throw new TimeoutException("Deployment wurde nicht fertig");
    }

    [Fact]
    public async Task Steps_keep_order_and_can_be_moved_and_deleted()
    {
        var package = await _packages.SaveAsync(null, "Demo", "1.0", null);
        foreach (var name in new[] { "a", "b", "c" })
            await _packages.SaveStepAsync(package.Id, null, Command(name), null);
        var steps = (await _packages.GetAsync(package.Id))!.Steps;

        await _packages.MoveStepAsync(package.Id, steps[2].Id, -1);
        await _packages.MoveStepAsync(package.Id, steps[0].Id, -1);
        await _packages.DeleteStepAsync(package.Id, steps[0].Id);

        Assert.Equal(["c", "b"], (await _packages.GetAsync(package.Id))!.Steps.Select(s => s.Definition.Name));
    }

    [Fact]
    public async Task BuildJob_flattens_nested_packages_and_prefixes_file_paths()
    {
        var inner = await _packages.SaveAsync(null, "7-Zip", "24.08", null);
        await _packages.Repository.SaveAsync(inner.Id, "7z.msi", new MemoryStream([1, 2, 3]));
        await _packages.SaveStepAsync(inner.Id, null, new JobStep { Name = "Installieren", Type = StepType.Install, File = "7z.msi", Arguments = "/qn" }, null);
        var outer = await _packages.SaveAsync(null, "Grundausstattung", null, null);
        await _packages.Repository.SaveAsync(outer.Id, @"config\app.ini", new MemoryStream([4]));
        await _packages.SaveStepAsync(outer.Id, null, Command("Vorbereiten"), null);
        await _packages.SaveStepAsync(outer.Id, null, new JobStep { Name = "7-Zip" }, inner.Id);
        await _packages.SaveStepAsync(outer.Id, null, new JobStep { Name = "Konfiguration", Type = StepType.FileCopy, File = @"config\app.ini", Destination = @"C:\App\" }, null);

        var built = await _packages.BuildJobAsync(outer.Id);

        Assert.Equal(["Vorbereiten", "7-Zip › Installieren", "Konfiguration"], built.Manifest.Steps.Select(s => s.Name));
        Assert.Equal($@"p{inner.Id}\7z.msi", built.Manifest.Steps[1].File);
        Assert.Equal($@"p{outer.Id}\config\app.ini", built.Manifest.Steps[2].File);
        Assert.Equal([$@"p{outer.Id}\config\app.ini", $@"p{inner.Id}\7z.msi"], built.Files.Select(f => f.RelativePath));
        Assert.All(built.Files, f => Assert.True(File.Exists(f.LocalPath)));
        // Das gespeicherte Paket bleibt unverändert.
        Assert.Equal("7z.msi", (await _packages.GetAsync(inner.Id))!.Steps[0].Definition.File);
    }

    [Fact]
    public async Task Nesting_a_package_into_itself_is_rejected()
    {
        var a = await _packages.SaveAsync(null, "A", null, null);
        var b = await _packages.SaveAsync(null, "B", null, null);
        await _packages.SaveStepAsync(a.Id, null, new JobStep { Name = "B" }, b.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _packages.SaveStepAsync(b.Id, null, new JobStep { Name = "A" }, a.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _packages.SaveStepAsync(a.Id, null, new JobStep { Name = "A" }, a.Id));
    }

    [Fact]
    public void Repository_rejects_paths_outside_the_package_folder()
    {
        var repository = new PackageRepository(_repoDir);

        Assert.Throws<ArgumentException>(() => repository.Resolve(1, @"..\2\fremd.msi"));
        Assert.Throws<ArgumentException>(() => repository.Resolve(1, "../../etc/passwd"));
    }

    [Fact]
    public async Task Deployment_runs_all_targets_stores_step_results_and_inventory()
    {
        var package = await _packages.SaveAsync(null, "Demo", "1.0", null);
        await _packages.SaveStepAsync(package.Id, null, Command("Erster"), null);
        await _packages.SaveStepAsync(package.Id, null, Command("Zweiter mit Fehler"), null);
        await _packages.SaveStepAsync(package.Id, null, Command("Dritter"), null);
        var computers = await _computers.ImportAsync("pc-01 pc-02");
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));
        var changes = 0;
        deployments.Changed += _ => Interlocked.Increment(ref changes);

        var started = await deployments.StartAsync(package.Id, computers.Select(c => c.Id), scanAfter: true);
        var finished = await WaitAsync(deployments, started.Id);

        Assert.Equal("Demo", finished.PackageName);
        Assert.All(finished.Targets, t =>
        {
            Assert.Equal(TargetStatus.Failed, t.Status);
            Assert.Equal("Exitcode 1", t.Error);
            Assert.Equal([StepStatus.Succeeded, StepStatus.Failed, StepStatus.Skipped, StepStatus.Skipped], t.Steps.Select(s => s.Status));
            Assert.NotNull(t.FinishedUtc);
        });
        Assert.True(changes >= 4);
        Assert.Null(deployments.GetLive(finished.Targets[0].Id));

        // Ohne den fehlschlagenden Schritt läuft auch der angehängte Scan und füllt das Inventar.
        await _packages.DeleteStepAsync(package.Id, (await _packages.GetAsync(package.Id))!.Steps[1].Id);
        var second = await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computers[0].Id], scanAfter: true)).Id);
        Assert.Equal(TargetStatus.Succeeded, Assert.Single(second.Targets).Status);
        Assert.NotNull((await _computers.GetAsync(computers[0].Id))!.LastScanUtc);
        Assert.Null((await _computers.GetAsync(computers[1].Id))!.LastScanUtc);
    }

    [Fact]
    public async Task Deployment_can_be_cancelled_and_empty_packages_are_rejected()
    {
        var empty = await _packages.SaveAsync(null, "Leer", null, null);
        var package = await _packages.SaveAsync(null, "Langsam", null, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Warten"), null);
        var computer = (await _computers.ImportAsync("pc-01"))[0];
        var transport = new BlockingTransport();
        var deployments = CreateDeployments(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => deployments.StartAsync(empty.Id, [computer.Id], false));

        var started = await deployments.StartAsync(package.Id, [computer.Id], false);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("zeile", deployments.GetLive(started.Targets[0].Id)!.Output.Trim().Split('\n').Last().Trim());
        deployments.Cancel(started.Id);
        var finished = await WaitAsync(deployments, started.Id);

        Assert.Equal(TargetStatus.Cancelled, Assert.Single(finished.Targets).Status);
    }

    [Fact]
    public async Task Interrupted_deployments_are_marked_failed_on_startup()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Deployments.Add(new Deployment
            {
                PackageName = "Alt",
                Targets = [new DeploymentTarget { ComputerName = "pc-01", Status = TargetStatus.Running }, new DeploymentTarget { ComputerName = "pc-02", Status = TargetStatus.Succeeded }],
            });
            await db.SaveChangesAsync();
        }
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));

        await deployments.MarkInterruptedAsync();

        var deployment = Assert.Single(await deployments.ListAsync());
        Assert.NotNull(deployment.FinishedUtc);
        Assert.Equal([TargetStatus.Failed, TargetStatus.Succeeded], deployment.Targets.OrderBy(t => t.ComputerName).Select(t => t.Status));
    }

    private sealed class BlockingTransport : IRemoteTransport
    {
        public TransportKind Kind => TransportKind.SmbService;
        public TaskCompletionSource Started { get; } = new();

        public async Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            progress?.Report(new ProgressEvent { Kind = ProgressKind.Output, Text = "zeile" });
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new JobResult();
        }
    }
}
