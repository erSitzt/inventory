using System.Collections.Concurrent;
using System.Text;
using Inventory.Contracts;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Legt Deployments an, führt sie im Hintergrund aus und hält den Live-Stand laufender Ziele bereit.</summary>
public sealed class DeploymentService(IDbContextFactory<InventoryDbContext> dbFactory, PackageService packages, RemoteJobService jobs, InventoryStore inventory)
{
    private const int MaxParallelTargets = 16;

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
    public async Task<Deployment> StartAsync(int packageId, IEnumerable<int> computerIds, bool scanAfter, CancellationToken ct = default)
    {
        var built = await packages.BuildJobAsync(packageId, ct);
        if (scanAfter)
            built.Manifest.Steps.Add(new JobStep { Name = "Inventar-Scan", Type = StepType.InventoryScan, TimeoutSeconds = 300 });

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
                Targets = computers.Select(c => new DeploymentTarget { ComputerId = c.Id, ComputerName = c.Name, Status = TargetStatus.Queued }).ToList(),
            };
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync(ct);
        }

        var cts = new CancellationTokenSource();
        _running[deployment.Id] = cts;
        _ = Task.Run(() => RunAsync(deployment, built, cts));
        return deployment;
    }

    /// <summary>Bricht laufende Ziele ab; noch wartende werden nicht mehr gestartet.</summary>
    public void Cancel(int deploymentId)
    {
        if (_running.TryGetValue(deploymentId, out var cts))
            cts.Cancel();
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

    private async Task RunAsync(Deployment deployment, BuiltJob built, CancellationTokenSource cts)
    {
        try
        {
            await Task.WhenAll(deployment.Targets.Select(target => RunTargetAsync(deployment, target, built, cts.Token)));
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

    private async Task RunTargetAsync(Deployment deployment, DeploymentTarget target, BuiltJob built, CancellationToken ct)
    {
        var status = TargetStatus.Failed;
        string? error = null;
        List<StepResult> steps = [];
        var started = false;
        try
        {
            await _slots.WaitAsync(ct);
            started = true;
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
                stored.FinishedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            });
            Changed?.Invoke(deployment.Id);
        }
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
