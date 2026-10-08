using System.Collections.Concurrent;
using System.Text;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Legt Deployments an, führt sie im Hintergrund aus und hält den Live-Stand laufender Ziele bereit.</summary>
public sealed class DeploymentService(IDbContextFactory<InventoryDbContext> dbFactory, PackageService packages, RemoteJobService jobs, InventoryStore inventory, IHostProbe probe)
{
    private const int MaxParallelTargets = 16;
    /// <summary>So lange werden Offline-Rechner nachgeholt, bevor das Ziel als fehlgeschlagen gilt.</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(3);
    private const string WaitingText = "Rechner nicht erreichbar – wird nachgeholt, sobald er online ist";

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
    public async Task<Deployment> StartAsync(int packageId, IEnumerable<int> computerIds, bool scanAfter, bool retryOffline = false, CancellationToken ct = default)
    {
        var built = await BuildAsync(packageId, scanAfter, ct);

        var ids = computerIds.Distinct().ToList();
        Deployment deployment;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var package = await db.Packages.AsNoTracking().FirstAsync(p => p.Id == packageId, ct);
            var computers = await db.Computers.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync(ct);
            if (computers.Count == 0)
                throw new InvalidOperationException("Keine Zielrechner ausgewählt.");
            deployment = new Deployment
            {
                PackageId = packageId, PackageName = package.Name, PackageVersion = package.Version, ScanAfter = scanAfter,
                RetryUntilUtc = retryOffline ? DateTime.UtcNow + RetryWindow : null,
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
        await UpdateAsync(db => db.DeploymentTargets.Where(t => t.DeploymentId == deploymentId && t.Status == TargetStatus.WaitingOffline)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Cancelled).SetProperty(t => t.Error, "Abgebrochen")
                .SetProperty(t => t.FinishedUtc, DateTime.UtcNow)));
        Changed?.Invoke(deploymentId);
    }

    /// <summary>
    /// Holt Ziele nach, die auf einen Offline-Rechner warten: wer jetzt erreichbar ist, wird ausgeführt;
    /// ist die Frist abgelaufen, gilt das Ziel als fehlgeschlagen. Wird regelmäßig vom Zeitplaner aufgerufen.
    /// </summary>
    public async Task<int> RetryWaitingAsync(CancellationToken ct = default)
    {
        List<Deployment> waiting;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            waiting = await db.Deployments.AsNoTracking()
                .Include(d => d.Targets.Where(t => t.Status == TargetStatus.WaitingOffline))
                .Where(d => d.Targets.Any(t => t.Status == TargetStatus.WaitingOffline))
                .ToListAsync(ct);
        }

        var started = 0;
        foreach (var deployment in waiting.Where(d => !_running.ContainsKey(d.Id)))
        {
            var expired = deployment.RetryUntilUtc is null || deployment.RetryUntilUtc < DateTime.UtcNow;
            var ready = new List<DeploymentTarget>();
            if (!expired)
            {
                foreach (var target in deployment.Targets)
                {
                    if (await IsReachableAsync(target.ComputerName, ct))
                        ready.Add(target);
                }
            }

            BuiltJob? built = null;
            string? failure = expired ? "Rechner war bis zum Ablauf der Frist nicht erreichbar" : null;
            if (ready.Count > 0)
            {
                try
                {
                    built = deployment.PackageId is { } packageId
                        ? await BuildAsync(packageId, deployment.ScanAfter, ct)
                        : throw new InvalidOperationException("Das Paket wurde gelöscht");
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    failure = ex.Message;
                }
            }

            if (failure is not null)
            {
                await UpdateAsync(db => db.DeploymentTargets.Where(t => t.DeploymentId == deployment.Id && t.Status == TargetStatus.WaitingOffline)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Failed).SetProperty(t => t.Error, failure)
                        .SetProperty(t => t.FinishedUtc, DateTime.UtcNow)));
                Changed?.Invoke(deployment.Id);
            }
            else if (built is not null)
            {
                Launch(deployment, ready, built, checkOnline: false);
                started += ready.Count;
            }
        }
        return started;
    }

    /// <summary>Nach einem Neustart des Servers: Ziele, die noch als wartend oder laufend gespeichert sind, wurden nie beendet.</summary>
    public async Task MarkInterruptedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        await db.DeploymentTargets.Where(t => t.Status == TargetStatus.Queued || t.Status == TargetStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Failed)
                .SetProperty(t => t.Error, "Der Server wurde während der Ausführung beendet")
                .SetProperty(t => t.FinishedUtc, now), ct);
        await db.Deployments.Where(d => d.FinishedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(d => d.FinishedUtc, now), ct);
    }

    private async Task RunAsync(Deployment deployment, IReadOnlyList<DeploymentTarget> targets, BuiltJob built, bool checkOnline, CancellationTokenSource cts)
    {
        try
        {
            await Task.WhenAll(targets.Select(target => RunTargetAsync(deployment, target, built, checkOnline, cts.Token)));
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

    private async Task RunTargetAsync(Deployment deployment, DeploymentTarget target, BuiltJob built, bool checkOnline, CancellationToken ct)
    {
        var status = TargetStatus.Failed;
        string? error = null;
        List<StepResult> steps = [];
        var started = false;
        try
        {
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
            var job = new JobManifest { Steps = built.Manifest.Steps };
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
                : JobResult.Failed(job.JobId, "Der Rechner wurde gelöscht");
            steps = result.Steps;
            error = result.Error ?? (result.Success ? null : steps.FirstOrDefault(s => s.Status == StepStatus.Failed)?.Error);
            status = result.Success ? TargetStatus.Succeeded : ct.IsCancellationRequested ? TargetStatus.Cancelled : TargetStatus.Failed;

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
                stored.FinishedUtc = status == TargetStatus.WaitingOffline ? null : DateTime.UtcNow;
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
