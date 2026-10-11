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
    private readonly SwitchableProbe _probe = new();

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
        new(_db, _packages, new RemoteJobService([transport], _computers, new CredentialService(_db, new ReversingProtector()), new SettingsService(_db)), new InventoryStore(_db), _probe);

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
        await deployments.CancelAsync(started.Id);
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

    [Fact]
    public async Task Offline_targets_wait_and_are_run_once_the_computer_is_reachable()
    {
        var package = await _packages.SaveAsync(null, "Demo", null, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Schritt"), null);
        var computers = await _computers.ImportAsync("pc-an pc-aus");
        _probe.Offline.Add("pc-aus");
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));

        var first = await WaitAsync(deployments, (await deployments.StartAsync(package.Id, computers.Select(c => c.Id), scanAfter: false, retryOffline: true)).Id);
        Assert.Equal([TargetStatus.Succeeded, TargetStatus.WaitingOffline], first.Targets.Select(t => t.Status));
        Assert.NotNull(first.RetryUntilUtc);

        // Solange der Rechner offline ist, passiert nichts.
        Assert.Equal(0, await deployments.RetryWaitingAsync());
        _probe.Offline.Clear();
        Assert.Equal(1, await deployments.RetryWaitingAsync());

        var second = await WaitAsync(deployments, first.Id);
        Assert.All(second.Targets, t => Assert.Equal(TargetStatus.Succeeded, t.Status));
        Assert.Equal(0, await deployments.RetryWaitingAsync());
    }

    [Fact]
    public async Task Waiting_targets_fail_after_the_retry_window_and_without_retry_offline_targets_fail_directly()
    {
        var package = await _packages.SaveAsync(null, "Demo", null, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Schritt"), null);
        var computer = (await _computers.ImportAsync("pc-aus"))[0];
        _probe.Offline.Add("pc-aus");
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));

        var waiting = await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], false, retryOffline: true)).Id);
        Assert.Equal(TargetStatus.WaitingOffline, waiting.Targets[0].Status);
        await using (var db = _db.CreateDbContext())
        {
            db.Deployments.Single(d => d.Id == waiting.Id).RetryUntilUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await deployments.RetryWaitingAsync();
        Assert.Equal(TargetStatus.Failed, (await deployments.GetAsync(waiting.Id))!.Targets[0].Status);

        // Ohne Nachholen wird nicht vorab geprüft: das Ziel läuft sofort (hier: Simulation erfolgreich).
        var direct = await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], false)).Id);
        Assert.Equal(TargetStatus.Succeeded, direct.Targets[0].Status);
    }

    [Fact]
    public async Task A_reboot_step_parks_the_target_and_the_remaining_steps_run_once_the_computer_is_back()
    {
        var package = await _packages.SaveAsync(null, "Treiber", "1.0", null);
        await _packages.SaveStepAsync(package.Id, null, Command("Vorbereiten"), null);
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Neustart", Type = StepType.Reboot, Seconds = 0 }, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Nachbereiten"), null);
        var computer = (await _computers.ImportAsync("pc-01"))[0];
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));

        var parked = await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], scanAfter: true)).Id);
        var target = parked.Targets[0];
        Assert.Equal(TargetStatus.WaitingReboot, target.Status);
        Assert.Equal(2, target.ResumeFromStep);
        Assert.NotNull(target.RebootUtc);
        Assert.NotNull(target.BootTimeUtc);
        Assert.Null(target.FinishedUtc);
        Assert.Equal([StepStatus.Succeeded, StepStatus.Succeeded, StepStatus.Deferred, StepStatus.Deferred], target.Steps.Select(s => s.Status));

        // Solange der Rechner nicht erreichbar ist, bleibt das Ziel geparkt.
        _probe.Offline.Add("pc-01");
        Assert.Equal(0, await deployments.ResumeRebootedAsync());
        _probe.Offline.Clear();
        Assert.Equal(1, await deployments.ResumeRebootedAsync());

        var done = await WaitAsync(deployments, parked.Id);
        target = done.Targets[0];
        Assert.Equal(TargetStatus.Succeeded, target.Status);
        Assert.Null(target.ResumeFromStep);
        Assert.Null(target.RebootUtc);
        Assert.NotNull(target.FinishedUtc);
        // Die Ergebnisse vor dem Neustart bleiben erhalten; die Fortsetzung hängt mit fortlaufenden Nummern an.
        Assert.Equal(["Vorbereiten", "Neustart", "Nachbereiten", "Inventar-Scan"], target.Steps.Select(s => s.Name));
        Assert.Equal([0, 1, 2, 3], target.Steps.Select(s => s.Index));
        Assert.All(target.Steps, s => Assert.Equal(StepStatus.Succeeded, s.Status));
        Assert.NotNull((await _computers.GetAsync(computer.Id))!.LastScanUtc);
        Assert.Equal(0, await deployments.ResumeRebootedAsync());
    }

    [Fact]
    public async Task A_computer_that_does_not_return_after_the_reboot_fails_after_the_window_and_cancel_stops_waiting()
    {
        var package = await _packages.SaveAsync(null, "Treiber", null, null);
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Neustart", Type = StepType.Reboot }, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Danach"), null);
        var computers = await _computers.ImportAsync("pc-01 pc-02");
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));

        var parked = await WaitAsync(deployments, (await deployments.StartAsync(package.Id, computers.Select(c => c.Id), false)).Id);
        Assert.All(parked.Targets, t => Assert.Equal(TargetStatus.WaitingReboot, t.Status));
        _probe.Offline.Add("pc-01");
        _probe.Offline.Add("pc-02");
        await using (var db = _db.CreateDbContext())
        {
            db.DeploymentTargets.Single(t => t.ComputerName == "pc-01").RebootUtc = DateTime.UtcNow - TimeSpan.FromMinutes(SettingsService.DefaultRebootWaitMinutes + 1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await deployments.ResumeRebootedAsync());
        var after = (await deployments.GetAsync(parked.Id))!;
        Assert.Equal(TargetStatus.Failed, after.Targets.Single(t => t.ComputerName == "pc-01").Status);
        Assert.Contains("did not come back", after.Targets.Single(t => t.ComputerName == "pc-01").Error);
        Assert.Equal(TargetStatus.WaitingReboot, after.Targets.Single(t => t.ComputerName == "pc-02").Status);

        await deployments.CancelAsync(parked.Id);
        Assert.Equal(TargetStatus.Cancelled, (await deployments.GetAsync(parked.Id))!.Targets.Single(t => t.ComputerName == "pc-02").Status);
    }

    [Fact]
    public async Task A_package_whose_detection_already_applies_is_skipped_and_reported_as_already_installed()
    {
        var installed = SimulatedInventory.Create("pc-01").Software[0];
        var package = await _packages.SaveAsync(null, "Vorhanden", installed.Version, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Installieren"), null);
        await _packages.SaveDetectionAsync(package.Id, new StepConditions { SoftwareInstalled = installed.Name, SoftwareMinVersion = installed.Version }, false);
        var computer = (await _computers.ImportAsync("pc-01"))[0];
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));

        var target = (await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], scanAfter: true)).Id)).Targets[0];
        Assert.Equal(TargetStatus.AlreadyInstalled, target.Status);
        Assert.True(target.Status.IsSuccess());
        Assert.All(target.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.NotNull(target.FinishedUtc);

        // Eine neuere Mindestversion ist nicht vorhanden: das Paket läuft.
        await _packages.SaveDetectionAsync(package.Id, new StepConditions { SoftwareInstalled = installed.Name, SoftwareMinVersion = "9999.0" }, false);
        var again = (await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], scanAfter: false)).Id)).Targets[0];
        Assert.Equal(TargetStatus.Succeeded, again.Status);
        Assert.Equal(StepStatus.Succeeded, Assert.Single(again.Steps).Status);
    }

    [Fact]
    public async Task Verification_fails_the_target_when_the_package_is_not_detected_after_the_last_step()
    {
        var package = await _packages.SaveAsync(null, "Kaputtes Setup", null, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Installieren"), null);
        await _packages.SaveDetectionAsync(package.Id, new StepConditions { SoftwareInstalled = "Fehler-App" }, verifyAfterInstall: true);
        var computer = (await _computers.ImportAsync("pc-01"))[0];
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));

        var target = (await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], false)).Id)).Targets[0];
        Assert.Equal(TargetStatus.Failed, target.Status);
        Assert.Contains("Verification failed", target.Error);
        Assert.Equal(StepStatus.Succeeded, Assert.Single(target.Steps).Status);

        // Eine leere Erkennung schaltet auch die Prüfung ab.
        await _packages.SaveDetectionAsync(package.Id, new StepConditions(), verifyAfterInstall: true);
        var stored = (await _packages.GetAsync(package.Id))!;
        Assert.Null(stored.Detection);
        Assert.False(stored.VerifyAfterInstall);
    }

    [Fact]
    public async Task After_a_reboot_the_remaining_steps_run_even_if_the_package_is_already_detected()
    {
        var package = await _packages.SaveAsync(null, "Mit Neustart", null, null);
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Neustart", Type = StepType.Reboot }, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Einrichten"), null);
        await _packages.SaveDetectionAsync(package.Id, new StepConditions { FileExists = @"C:\App\app.exe" }, verifyAfterInstall: true);
        var computer = (await _computers.ImportAsync("pc-01"))[0];
        var transport = new RecordingTransport();
        var deployments = CreateDeployments(transport);

        var id = (await deployments.StartAsync(package.Id, [computer.Id], false)).Id;
        await WaitAsync(deployments, id);
        Assert.Equal(1, await deployments.ResumeRebootedAsync());
        await WaitAsync(deployments, id);

        Assert.Equal([true, false], transport.Jobs.Select(j => j.SkipIfDetected));
        Assert.All(transport.Jobs, j => Assert.True(j.VerifyAfterInstall && j.Detection?.FileExists == @"C:\App\app.exe"));
    }

    [Fact]
    public async Task The_wait_for_a_restart_follows_the_setting()
    {
        var package = await _packages.SaveAsync(null, "Langsamer Neustart", null, null);
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Neustart", Type = StepType.Reboot }, null);
        await _packages.SaveStepAsync(package.Id, null, Command("Danach"), null);
        var computer = (await _computers.ImportAsync("pc-01"))[0];
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));
        var settings = new SettingsService(_db);
        Assert.Equal(SettingsService.DefaultRebootWaitMinutes, await settings.GetRebootWaitMinutesAsync());

        var parked = await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], false)).Id);
        _probe.Offline.Add("pc-01");
        await using (var db = _db.CreateDbContext())
        {
            db.DeploymentTargets.Single().RebootUtc = DateTime.UtcNow - TimeSpan.FromMinutes(45);
            await db.SaveChangesAsync();
        }

        await settings.SetRebootWaitMinutesAsync(60);
        await deployments.ResumeRebootedAsync();
        Assert.Equal(TargetStatus.WaitingReboot, (await deployments.GetAsync(parked.Id))!.Targets[0].Status);

        await settings.SetRebootWaitMinutesAsync(40);
        await deployments.ResumeRebootedAsync();
        var failed = (await deployments.GetAsync(parked.Id))!.Targets[0];
        Assert.Equal(TargetStatus.Failed, failed.Status);
        Assert.Contains("40 minutes", failed.Error);
    }

    [Fact]
    public async Task An_uninstall_package_is_created_once_and_reused()
    {
        var (package, created) = await _packages.EnsureUninstallPackageAsync("7-Zip", "7-Zip", null);
        Assert.True(created);
        Assert.Equal("Uninstall 7-Zip", package.Name);
        var step = Assert.Single((await _packages.GetAsync(package.Id))!.Steps).Definition;
        Assert.Equal((StepType.Uninstall, "7-Zip", null), (step.Type, step.SoftwareName, step.ProductCode));

        var (again, createdAgain) = await _packages.EnsureUninstallPackageAsync(" 7-Zip ", "etwas anderes", "{code}");
        Assert.False(createdAgain);
        Assert.Equal(package.Id, again.Id);

        // Der Schritt-Typ übersteht Speichern und Laden und läuft in der Simulation durch.
        var computer = (await _computers.ImportAsync("pc-01"))[0];
        var deployments = CreateDeployments(new SimulatedTransport(TransportKind.SmbService));
        var target = (await WaitAsync(deployments, (await deployments.StartAsync(package.Id, [computer.Id], false)).Id)).Targets[0];
        Assert.Equal(TargetStatus.Succeeded, target.Status);
    }

    /// <summary>Merkt sich die Manifeste; der erste Job endet mit einem Neustart, der zweite läuft durch.</summary>
    private sealed class RecordingTransport : IRemoteTransport
    {
        public TransportKind Kind => TransportKind.SmbService;
        public List<JobManifest> Jobs { get; } = [];

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            Jobs.Add(job);
            var first = Jobs.Count == 1;
            return Task.FromResult(new JobResult
            {
                JobId = job.JobId, Success = true, ResumeFromStep = first ? 1 : null, BootTimeUtc = DateTime.UtcNow.AddMinutes(first ? -60 : 0),
                Steps = [.. job.Steps.Select((s, i) => new StepResult { Index = i, Name = s.Name, Status = first && i > 0 ? StepStatus.Deferred : StepStatus.Succeeded })],
            });
        }
    }

    private sealed class SwitchableProbe : IHostProbe
    {
        public HashSet<string> Offline { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default) =>
            Task.FromResult(new ProbeResult(!Offline.Contains(host), "10.0.0.1"));
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
