using System.Collections.Concurrent;
using System.Text;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Legt Deployments an, führt sie im Hintergrund aus und hält den Live-Stand laufender Ziele bereit.</summary>
public sealed class DeploymentService(IDbContextFactory<InventoryDbContext> dbFactory, PackageService packages, RemoteJobService jobs, InventoryStore inventory, IHostProbe probe, IStringLocalizer<SharedResource>? localizer = null, MaintenanceService? maintenance = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    private readonly MaintenanceService _maintenance = maintenance ?? new MaintenanceService(dbFactory, new CollectionService(dbFactory), new SettingsService(dbFactory));


    private const int MaxParallelTargets = 16;
    /// <summary>So lange werden Offline-Rechner nachgeholt, bevor das Ziel als fehlgeschlagen gilt.</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(3);
    /// <summary>Frist für Ziele, die auf ihr Wartungsfenster warten: auch ein Fenster, das nur einmal je Woche öffnet, muss hineinpassen.</summary>
    public static readonly TimeSpan WindowWait = TimeSpan.FromDays(8);
    private string WaitingText => L["Computer unreachable – will be retried as soon as it is online"];

    // Über alle Deployments hinweg begrenzt, damit mehrere gleichzeitige Deployments Server und Netz nicht überlasten.
    private readonly SemaphoreSlim _slots = new(MaxParallelTargets, MaxParallelTargets);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<int, LiveTarget> _live = new();

    /// <summary>Wird aus Hintergrund-Threads ausgelöst, sobald sich an einem Deployment etwas ändert.</summary>
    public event Action<int>? Changed;

    public sealed class LiveTarget
    {
        private readonly StringBuilder _output = new();
        public string? CurrentStep { get; internal set; }

        public string Output
        {
            get { lock (_output) return _output.ToString(); }
        }

        internal void Append(string? line)
        {
            lock (_output)
                _output.AppendLine(line);
        }
    }

    /// <summary>Ausgabe eines gerade laufenden Ziels; null, wenn es nicht (mehr) läuft.</summary>
    public LiveTarget? GetLive(int targetId) => _live.GetValueOrDefault(targetId);

    public bool IsRunning(int deploymentId) => _running.ContainsKey(deploymentId);

    /// <summary>true, solange irgendein Deployment Ziele ausführt.</summary>
    public bool AnyRunning => !_running.IsEmpty;

    public async Task<List<Deployment>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Deployments.AsNoTracking().Include(d => d.Targets).OrderByDescending(d => d.Id).Take(take).ToListAsync(ct);
    }

    public async Task<Deployment?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Deployments.AsNoTracking().Include(d => d.Targets.OrderBy(t => t.ComputerName)).FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    /// <summary>Legt das Deployment an und startet es im Hintergrund.</summary>
    /// <exception cref="InvalidOperationException">Das Paket lässt sich nicht ausführen (z. B. keine Schritte).</exception>
    /// <param name="respectWindow">Rechner außerhalb ihres Wartungsfensters warten, bis es beginnt (höchstens <see cref="WindowWait"/>).</param>
    public async Task<Deployment> StartAsync(int packageId, IEnumerable<int> computerIds, bool scanAfter, bool retryOffline = false, CancellationToken ct = default, bool respectWindow = false)
    {
        var built = await BuildAsync(packageId, scanAfter, ct);

        var ids = computerIds.Distinct().ToList();
        Deployment deployment;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var package = await db.Packages.AsNoTracking().FirstAsync(p => p.Id == packageId, ct);
            var computers = await db.Computers.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync(ct);
            if (computers.Count == 0)
                throw new InvalidOperationException(L["No target computers selected."]);
            deployment = new Deployment
            {
                PackageId = packageId, PackageName = package.Name, PackageVersion = package.Version, ScanAfter = scanAfter,
                RetryUntilUtc = respectWindow ? DateTime.UtcNow + WindowWait : retryOffline ? DateTime.UtcNow + RetryWindow : null,
                RetryOffline = retryOffline, RespectMaintenanceWindow = respectWindow,
                Targets = computers.Select(c => new DeploymentTarget { ComputerId = c.Id, ComputerName = c.Name, Status = TargetStatus.Queued }).ToList(),
            };
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync(ct);
        }

        Launch(deployment, deployment.Targets, built, checkOnline: retryOffline);
        return deployment;
    }

    private async Task<BuiltJob> BuildAsync(int packageId, bool scanAfter, CancellationToken ct)
    {
        var built = await packages.BuildJobAsync(packageId, ct);
        if (scanAfter)
            built.Manifest.Steps.Add(new JobStep { Name = "Inventar-Scan", Type = StepType.InventoryScan, TimeoutSeconds = 300 });
        return built;
    }

    private void Launch(Deployment deployment, IReadOnlyList<DeploymentTarget> targets, BuiltJob built, bool checkOnline)
    {
        var cts = new CancellationTokenSource();
        _running[deployment.Id] = cts;
        _ = Task.Run(() => RunAsync(deployment, targets, built, checkOnline, cts));
    }

    /// <summary>Bricht laufende Ziele ab; wartende (auch auf Offline-Rechner) werden nicht mehr gestartet.</summary>
    public async Task CancelAsync(int deploymentId)
    {
        if (_running.TryGetValue(deploymentId, out var cts))
            cts.Cancel();
        await UpdateAsync(db => db.DeploymentTargets.Where(t => t.DeploymentId == deploymentId
                && (t.Status == TargetStatus.WaitingOffline || t.Status == TargetStatus.WaitingReboot || t.Status == TargetStatus.WaitingWindow))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Cancelled).SetProperty(t => t.Error, "Abgebrochen")
                .SetProperty(t => t.FinishedUtc, DateTime.UtcNow)));
        Changed?.Invoke(deploymentId);
    }

    /// <summary>
    /// Holt wartende Ziele nach: Offline-Rechner, die jetzt erreichbar sind, und Rechner, deren Wartungsfenster jetzt offen ist.
    /// Ist die Frist abgelaufen, gilt das Ziel als fehlgeschlagen. Wird regelmäßig vom Zeitplaner aufgerufen.
    /// </summary>
    public async Task<int> RetryWaitingAsync(CancellationToken ct = default)
    {
        List<Deployment> waiting;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            waiting = await db.Deployments.AsNoTracking()
                .Include(d => d.Targets.Where(t => t.Status == TargetStatus.WaitingOffline || t.Status == TargetStatus.WaitingWindow))
                .Where(d => d.Targets.Any(t => t.Status == TargetStatus.WaitingOffline || t.Status == TargetStatus.WaitingWindow))
                .ToListAsync(ct);
        }

        var started = 0;
        foreach (var deployment in waiting.Where(d => !_running.ContainsKey(d.Id)))
        {
            var expired = deployment.RetryUntilUtc is null || deployment.RetryUntilUtc < DateTime.UtcNow;
            var ready = new List<DeploymentTarget>();
            if (!expired)
            {
                var closed = deployment.RespectMaintenanceWindow
                    ? await _maintenance.ClosedAsync([.. deployment.Targets.Where(t => t.ComputerId is not null).Select(t => t.ComputerId!.Value)], ct)
                    : [];
                foreach (var target in deployment.Targets)
                {
                    if (target.ComputerId is { } id && closed.ContainsKey(id))
                        continue;
                    // Wer auf sein Fenster gewartet hat, wird beim Start ohnehin auf Erreichbarkeit geprüft.
                    if (target.Status == TargetStatus.WaitingWindow || await IsReachableAsync(target.ComputerName, ct))
                        ready.Add(target);
                }
            }

            BuiltJob? built = null;
            string? failure = null;
            if (ready.Count > 0)
            {
                try
                {
                    built = deployment.PackageId is { } packageId
                        ? await BuildAsync(packageId, deployment.ScanAfter, ct)
                        : throw new InvalidOperationException(L["The package was deleted"]);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    failure = ex.Message;
                }
            }

            if (expired || failure is not null)
            {
                var offline = failure ?? L["Computer was unreachable until the deadline expired"].Value;
                var window = failure ?? L["The maintenance window did not open before the deadline expired"].Value;
                await UpdateAsync(async db =>
                {
                    await db.DeploymentTargets.Where(t => t.DeploymentId == deployment.Id && t.Status == TargetStatus.WaitingOffline)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Failed).SetProperty(t => t.Error, offline).SetProperty(t => t.FinishedUtc, DateTime.UtcNow));
                    await db.DeploymentTargets.Where(t => t.DeploymentId == deployment.Id && t.Status == TargetStatus.WaitingWindow)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Failed).SetProperty(t => t.Error, window).SetProperty(t => t.FinishedUtc, DateTime.UtcNow));
                });
                Changed?.Invoke(deployment.Id);
            }
            else if (built is not null)
            {
                Launch(deployment, ready, built, checkOnline: deployment.RetryOffline);
                started += ready.Count;
            }
        }
        return started;
    }

    /// <summary>
    /// Setzt Ziele fort, deren Rechner nach einem Neustart-Schritt wieder erreichbar ist: der Folgejob enthält die restlichen
    /// Schritte und läuft nur, wenn der Rechner wirklich neu gestartet ist. Nach der eingestellten Wartefrist
    /// (<see cref="SettingKeys.RebootWaitMinutes"/>) gilt das Ziel als fehlgeschlagen.
    /// Wird vom Zeitplaner in jedem Takt aufgerufen.
    /// </summary>
    public async Task<int> ResumeRebootedAsync(CancellationToken ct = default)
    {
        List<Deployment> waiting;
        TimeSpan rebootWindow;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            waiting = await db.Deployments.AsNoTracking()
                .Include(d => d.Targets.Where(t => t.Status == TargetStatus.WaitingReboot))
                .Where(d => d.Targets.Any(t => t.Status == TargetStatus.WaitingReboot))
                .ToListAsync(ct);
            if (waiting.Count == 0)
                return 0;
            rebootWindow = TimeSpan.FromMinutes(await SettingsService.RebootWaitMinutesAsync(db, ct));
        }

        var started = 0;
        foreach (var deployment in waiting.Where(d => !_running.ContainsKey(d.Id)))
        {
            var ready = new List<DeploymentTarget>();
            foreach (var target in deployment.Targets)
            {
                if (target.RebootUtc is { } rebooted && DateTime.UtcNow - rebooted > rebootWindow)
                {
                    var timeout = L["The computer did not come back within {0} minutes after the restart", (int)rebootWindow.TotalMinutes].Value;
                    await UpdateAsync(db => db.DeploymentTargets.Where(t => t.Id == target.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Failed).SetProperty(t => t.Error, timeout)
                            .SetProperty(t => t.FinishedUtc, DateTime.UtcNow)));
                    Changed?.Invoke(deployment.Id);
                }
                else if (await IsReachableAsync(target.ComputerName, ct))
                {
                    ready.Add(target);
                }
            }
            if (ready.Count == 0)
                continue;

            BuiltJob built;
            try
            {
                built = deployment.PackageId is { } packageId
                    ? await BuildAsync(packageId, deployment.ScanAfter, ct)
                    : throw new InvalidOperationException(L["The package was deleted"]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                var failure = ex.Message;
                await UpdateAsync(db => db.DeploymentTargets.Where(t => t.DeploymentId == deployment.Id && t.Status == TargetStatus.WaitingReboot)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Failed).SetProperty(t => t.Error, failure)
                        .SetProperty(t => t.FinishedUtc, DateTime.UtcNow)));
                Changed?.Invoke(deployment.Id);
                continue;
            }
            Launch(deployment, ready, built, checkOnline: false);
            started += ready.Count;
        }
        return started;
    }

    /// <summary>Nach einem Neustart des Servers: Ziele, die noch als wartend oder laufend gespeichert sind, wurden nie beendet.</summary>
    public async Task MarkInterruptedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var serverStopped = L["The server was stopped during execution"].Value;
        await db.DeploymentTargets.Where(t => t.Status == TargetStatus.Queued || t.Status == TargetStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Failed)
                .SetProperty(t => t.Error, serverStopped)
                .SetProperty(t => t.FinishedUtc, now), ct);
        await db.Deployments.Where(d => d.FinishedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(d => d.FinishedUtc, now), ct);
    }

    private async Task RunAsync(Deployment deployment, IReadOnlyList<DeploymentTarget> targets, BuiltJob built, bool checkOnline, CancellationTokenSource cts)
    {
        try
        {
            // Einmal für alle Ziele: die Mitgliedschaft in dynamischen Sammlungen auszuwerten ist zu teuer für jeden Rechner einzeln.
            // Eine Fortsetzung nach einem Neustart wartet nicht: ein halb installiertes Paket wäre schlimmer als ein überzogenes Fenster.
            var closed = deployment.RespectMaintenanceWindow
                ? await _maintenance.ClosedAsync([.. targets.Where(t => t.ComputerId is not null && t.Status != TargetStatus.WaitingReboot).Select(t => t.ComputerId!.Value)])
                : [];
            await Task.WhenAll(targets.Select(target => RunTargetAsync(deployment, target, built, checkOnline, closed, cts.Token)));
        }
        finally
        {
            await UpdateAsync(db => db.Deployments.Where(d => d.Id == deployment.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.FinishedUtc, DateTime.UtcNow)));
            _running.TryRemove(deployment.Id, out _);
            cts.Dispose();
            Changed?.Invoke(deployment.Id);
        }
    }

    private async Task RunTargetAsync(Deployment deployment, DeploymentTarget target, BuiltJob built, bool checkOnline, Dictionary<int, DateTime?> closed, CancellationToken ct)
    {
        var status = TargetStatus.Failed;
        string? error = null;
        List<StepResult> steps = [];
        var started = false;
        // Fortsetzung nach einem Neustart: nur die restlichen Schritte, Ergebnisse der bisherigen bleiben erhalten.
        var resumeFrom = target.Status == TargetStatus.WaitingReboot ? target.ResumeFromStep ?? 0 : 0;
        var previousSteps = resumeFrom > 0 ? target.Steps.Take(resumeFrom).ToList() : [];
        int? resumeNext = null;
        DateTime? bootTime = target.BootTimeUtc;
        try
        {
            if (target.ComputerId is { } windowId && closed.TryGetValue(windowId, out var opens))
            {
                status = TargetStatus.WaitingWindow;
                error = opens is { } opensAt
                    ? L["Waiting for the maintenance window (opens {0})", opensAt.ToString("yyyy-MM-dd HH:mm")]
                    : L["Waiting for the maintenance window"];
                return;
            }
            await _slots.WaitAsync(ct);
            started = true;
            if (checkOnline && !await IsReachableAsync(target.ComputerName, ct))
            {
                status = TargetStatus.WaitingOffline;
                error = WaitingText;
                return;
            }
            var live = _live[target.Id] = new LiveTarget();
            await UpdateAsync(db => db.DeploymentTargets.Where(t => t.Id == target.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Running).SetProperty(t => t.StartedUtc, DateTime.UtcNow)));
            Changed?.Invoke(deployment.Id);

            // Jedes Ziel braucht eine eigene Job-ID: sie benennt Dienst und Verzeichnis auf dem Zielrechner.
            var job = new JobManifest
            {
                Steps = built.Manifest.Steps.Skip(resumeFrom).ToList(),
                RequireBootAfterUtc = resumeFrom > 0 ? target.BootTimeUtc : null,
                Detection = built.Manifest.Detection,
                VerifyAfterInstall = built.Manifest.VerifyAfterInstall,
                // Nach einem Neustart ist das Paket oft schon erkennbar, obwohl noch Schritte ausstehen: dann nicht überspringen.
                SkipIfDetected = resumeFrom == 0,
            };
            var progress = new CallbackProgress(e =>
            {
                if (e.Kind == ProgressKind.StepStarted)
                {
                    live.CurrentStep = e.Text;
                    live.Append($"── {e.Text}");
                    Changed?.Invoke(deployment.Id);
                }
                else if (e.Kind == ProgressKind.Output)
                {
                    live.Append(e.Text);
                }
            });

            var result = target.ComputerId is { } computerId
                ? await jobs.RunAsync(computerId, job, progress, ct, built.Files)
                : JobResult.Failed(job.JobId, L["The computer was deleted"]);
            if (result.NotRebootedYet)
            {
                // Der Rechner läuft noch mit dem alten Systemstart (z. B. Verzögerung des Neustarts): weiter warten.
                status = TargetStatus.WaitingReboot;
                steps = target.Steps;
                resumeNext = resumeFrom;
                return;
            }
            foreach (var step in result.Steps)
                step.Index += resumeFrom;
            steps = [.. previousSteps, .. result.Steps];
            bootTime = result.BootTimeUtc ?? bootTime;
            error = result.Error ?? (result.Success ? null : steps.FirstOrDefault(s => s.Status == StepStatus.Failed)?.Error);
            status = result.Success ? TargetStatus.Succeeded : ct.IsCancellationRequested ? TargetStatus.Cancelled : TargetStatus.Failed;
            if (result.Success && result.AlreadyInstalled)
            {
                status = TargetStatus.AlreadyInstalled;
                error = L["Already installed – nothing was executed"];
            }
            if (result.Success && result.ResumeFromStep is { } next)
            {
                // Der Neustart läuft; die restlichen Schritte folgen, sobald der Rechner wieder erreichbar ist.
                status = TargetStatus.WaitingReboot;
                resumeNext = resumeFrom + next;
                error = L["Restarting – the remaining steps follow once the computer is back"];
            }

            if (result.Inventory is not null && target.ComputerId is { } scannedId)
                await inventory.SaveAsync(scannedId, result.Inventory, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            status = TargetStatus.Cancelled;
            error = "Abgebrochen";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            if (started)
                _slots.Release();
            _live.TryRemove(target.Id, out _);
            await UpdateAsync(async db =>
            {
                var stored = await db.DeploymentTargets.FirstAsync(t => t.Id == target.Id);
                stored.Status = status;
                stored.Error = error;
                stored.Steps = steps;
                stored.ResumeFromStep = status == TargetStatus.WaitingReboot ? resumeNext : null;
                stored.BootTimeUtc = bootTime;
                if (status == TargetStatus.WaitingReboot)
                    stored.RebootUtc ??= DateTime.UtcNow;
                else
                    stored.RebootUtc = null;
                stored.FinishedUtc = status.IsWaiting() ? null : DateTime.UtcNow;
                await db.SaveChangesAsync();
            });
            Changed?.Invoke(deployment.Id);
        }
    }

    /// <summary>Erreichbar heißt: es antwortet der gemeinte Rechner, nicht bloß irgendeiner unter seiner alten Adresse.</summary>
    private async Task<bool> IsReachableAsync(string computerName, CancellationToken ct)
    {
        var result = await probe.ProbeAsync(computerName, ct);
        if (!result.Online || !MachineNames.Differs(computerName, result.MachineName))
            return result.Online;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return !await SettingsService.VerifyTargetNameAsync(db, ct);
    }

    private async Task UpdateAsync(Func<InventoryDbContext, Task> update)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await update(db);
    }

    private sealed class CallbackProgress(Action<ProgressEvent> handler) : IProgress<ProgressEvent>
    {
        public void Report(ProgressEvent value) => handler(value);
    }
}
