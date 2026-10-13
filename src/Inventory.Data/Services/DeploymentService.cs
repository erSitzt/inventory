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
/// <summary>Einstellungen eines Deployments; die Grenzen sind optional (null = keine).</summary>
public sealed record DeploymentOptions(bool ScanAfter, bool RetryOffline = false, bool RespectWindow = false,
    int? MaxFailures = null, int? MaxFailurePercent = null, int? MaxParallel = null)
{
    /// <summary>true = nur freigegebene Pakete; gilt für Operatoren und Zeitpläne, nicht für Administratoren.</summary>
    public bool RequireApproved { get; init; }
    /// <summary>Nicht erreichbare Rechner per Wake-on-LAN wecken; sie warten dann wie mit <see cref="RetryOffline"/>.</summary>
    public bool Wake { get; init; }
    /// <summary>Der auslösende Zeitplan, damit sich später sagen lässt, auf welchen Rechnern er schon erfolgreich war.</summary>
    public int? ScheduleId { get; init; }
}

public sealed class DeploymentService(IDbContextFactory<InventoryDbContext> dbFactory, PackageService packages, RemoteJobService jobs, InventoryStore inventory, IHostProbe probe, IStringLocalizer<SharedResource>? localizer = null, MaintenanceService? maintenance = null, WakeService? wake = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    private readonly MaintenanceService _maintenance = maintenance ?? new MaintenanceService(dbFactory, new CollectionService(dbFactory), new SettingsService(dbFactory));


    /// <summary>So lange werden Offline-Rechner nachgeholt, bevor das Ziel als fehlgeschlagen gilt.</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(3);
    /// <summary>Frist für Ziele, die auf ihr Wartungsfenster warten: auch ein Fenster, das nur einmal je Woche öffnet, muss hineinpassen.</summary>
    public static readonly TimeSpan WindowWait = TimeSpan.FromDays(8);
    /// <summary>Ein wartender Rechner wird höchstens so oft erneut geweckt: wer ihn bewusst ausschaltet, soll ihn nicht alle fünf Minuten wieder laufen sehen.</summary>
    public static readonly TimeSpan WakeAgain = TimeSpan.FromHours(1);
    private string WaitingText => L["Computer unreachable – will be retried as soon as it is online"];
    private string WokenText => L["Computer unreachable – wake-up sent, will be retried as soon as it is online"];
    /// <summary>Wann welcher Rechner zuletzt geweckt wurde; nur im Speicher, nach einem Neustart des Servers wird einmal mehr geweckt.</summary>
    private readonly ConcurrentDictionary<int, DateTime> _woken = new();
    private long _recheckTicks;

    /// <summary>
    /// true, sobald geweckte Rechner Zeit zum Hochfahren hatten: der Zeitplaner holt die wartenden Ziele dann sofort nach,
    /// statt bis zum nächsten regulären Durchlauf zu warten. Der Aufruf verbraucht die Vormerkung.
    /// </summary>
    public bool TakeWakeRecheck(DateTime utcNow)
    {
        var due = Interlocked.Read(ref _recheckTicks);
        return due != 0 && utcNow.Ticks >= due && Interlocked.CompareExchange(ref _recheckTicks, 0, due) == due;
    }

    /// <summary>Weckt den Rechner, wenn das Deployment es vorsieht und er nicht gerade erst geweckt wurde.</summary>
    private async Task<bool> WakeAsync(Deployment deployment, DeploymentTarget target, CancellationToken ct)
    {
        if (!deployment.WakeTargets || wake is null || target.ComputerId is not { } computerId)
            return false;
        var now = DateTime.UtcNow;
        if (_woken.TryGetValue(computerId, out var last) && now - last < WakeAgain)
            return true;
        if ((await wake.WakeAsync(computerId, ct))?.Sent != true)
            return false;
        _woken[computerId] = now;
        // Mehrere Rechner desselben Durchlaufs teilen sich die früheste Nachprüfung.
        var due = (now + WakeService.BootWait).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _recheckTicks);
        } while ((current == 0 || due < current) && Interlocked.CompareExchange(ref _recheckTicks, due, current) != current);
        return true;
    }

    // Über alle Deployments hinweg begrenzt, damit mehrere gleichzeitige Deployments Server und Netz nicht überlasten.
    private readonly ParallelGate _slots = new(SettingsService.DefaultMaxParallelTargets);
    private readonly SettingsService _settings = new(dbFactory);
    /// <summary>Deployments, deren Fehlergrenze erreicht ist, mit dem Grund; ihre noch wartenden Ziele starten nicht mehr.</summary>
    private readonly ConcurrentDictionary<int, string> _stopped = new();
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
    public Task<Deployment> StartAsync(int packageId, IEnumerable<int> computerIds, bool scanAfter, bool retryOffline = false, CancellationToken ct = default, bool respectWindow = false) =>
        StartAsync(packageId, computerIds, new DeploymentOptions(scanAfter, retryOffline, respectWindow), ct);

    /// <summary>Legt das Deployment an und startet es im Hintergrund.</summary>
    /// <exception cref="InvalidOperationException">Das Paket lässt sich nicht ausführen (z. B. keine Schritte).</exception>
    public async Task<Deployment> StartAsync(int packageId, IEnumerable<int> computerIds, DeploymentOptions options, CancellationToken ct = default)
    {
        // Wecken ohne Warten wäre sinnlos: der Rechner braucht Minuten, bis er antwortet.
        var (scanAfter, retryOffline, respectWindow) = (options.ScanAfter, options.RetryOffline || options.Wake, options.RespectWindow);
        // Vor und nach dem Zusammenstellen prüfen: wer ein Paket ändert, zieht zuerst die Freigabe zurück und ändert dann den Inhalt.
        if (options.RequireApproved)
            await DemandApprovedAsync(packageId, [packageId], ct);
        var built = await BuildAsync(packageId, scanAfter, ct);
        if (options.RequireApproved)
            await DemandApprovedAsync(packageId, built.PackageIds, ct);

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
                RetryOffline = retryOffline, RespectMaintenanceWindow = respectWindow, WakeTargets = options.Wake,
                MaxFailures = options.MaxFailures is > 0 ? options.MaxFailures : null,
                MaxFailurePercent = options.MaxFailurePercent is > 0 ? Math.Min(options.MaxFailurePercent.Value, 100) : null,
                MaxParallel = options.MaxParallel is > 0 ? options.MaxParallel : null,
                ScheduleId = options.ScheduleId,
                Targets = computers.Select(c => new DeploymentTarget { ComputerId = c.Id, ComputerName = c.Name, Status = TargetStatus.Queued }).ToList(),
            };
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync(ct);
        }

        Launch(deployment, deployment.Targets, built, checkOnline: retryOffline);
        return deployment;
    }

    /// <summary>Das verteilte Paket und alle eingebetteten müssen freigegeben sein: ihr Inhalt läuft mit.</summary>
    private async Task DemandApprovedAsync(int packageId, IReadOnlyCollection<int> included, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.Packages.AsNoTracking().Where(p => included.Contains(p.Id) && p.ApprovedUtc == null)
            .OrderBy(p => p.Id == packageId ? 0 : 1).Select(p => p.Name).FirstOrDefaultAsync(ct);
        if (pending is not null)
            throw new InvalidOperationException(L["The package \"{0}\" is not approved for deployment.", pending]);
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
                    else if (await WakeAsync(deployment, target, ct) && target.Error != WokenText)
                    {
                        var woken = WokenText;
                        await UpdateAsync(db => db.DeploymentTargets.Where(t => t.Id == target.Id && t.Status == TargetStatus.WaitingOffline)
                            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Error, woken)));
                    }
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
                await CheckFailureLimitAsync(deployment);
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
                    await CheckFailureLimitAsync(deployment);
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
                await CheckFailureLimitAsync(deployment);
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
        SemaphoreSlim? own = null;
        try
        {
            // Einmal für alle Ziele: die Mitgliedschaft in dynamischen Sammlungen auszuwerten ist zu teuer für jeden Rechner einzeln.
            // Eine Fortsetzung nach einem Neustart wartet nicht: ein halb installiertes Paket wäre schlimmer als ein überzogenes Fenster.
            var closed = deployment.RespectMaintenanceWindow
                ? await _maintenance.ClosedAsync([.. targets.Where(t => t.ComputerId is not null && t.Status != TargetStatus.WaitingReboot).Select(t => t.ComputerId!.Value)])
                : [];
            _slots.Limit = await _settings.GetMaxParallelTargetsAsync();
            // Eigene Grenze des Deployments: die ersten Rechner wirken so als Pilotgruppe, bevor die übrigen folgen.
            own = deployment.MaxParallel is > 0 ? new SemaphoreSlim(deployment.MaxParallel.Value) : null;
            await Task.WhenAll(targets.Select(target => RunTargetAsync(deployment, target, built, checkOnline, closed, own, cts.Token)));
        }
        finally
        {
            await UpdateAsync(db => db.Deployments.Where(d => d.Id == deployment.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.FinishedUtc, DateTime.UtcNow)));
            _running.TryRemove(deployment.Id, out _);
            cts.Dispose();
            own?.Dispose();
            Changed?.Invoke(deployment.Id);
        }
    }

    /// <summary>
    /// Nach einem fehlgeschlagenen Ziel: ist die Fehlergrenze des Deployments erreicht, starten die noch wartenden Ziele nicht mehr.
    /// Laufende Ziele werden nicht abgebrochen – ein mitten in der Installation beendetes Setup wäre der größere Schaden.
    /// </summary>
    private async Task CheckFailureLimitAsync(Deployment deployment)
    {
        // Auch Ziele, die an einer Frist scheitern (Rechner blieb offline, Fenster öffnete nicht, Neustart kam nicht zurück), zählen mit.
        if ((deployment.MaxFailures ?? deployment.MaxFailurePercent) is null || deployment.StopReason is not null || _stopped.ContainsKey(deployment.Id))
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var failed = await db.DeploymentTargets.CountAsync(t => t.DeploymentId == deployment.Id && t.Status == TargetStatus.Failed);
        var finished = failed + await db.DeploymentTargets.CountAsync(t => t.DeploymentId == deployment.Id
            && (t.Status == TargetStatus.Succeeded || t.Status == TargetStatus.AlreadyInstalled));
        if (!DeploymentLimits.Reached(deployment.MaxFailures, deployment.MaxFailurePercent, failed, finished))
            return;

        var reason = L["Stopped: {0} of {1} finished computers failed", failed, finished].Value;
        if (!_stopped.TryAdd(deployment.Id, reason))
            return;
        await db.Deployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.StopReason, reason));
        // Wer noch auf seinen Rechner oder sein Fenster wartet, würde sonst später doch noch starten.
        await db.DeploymentTargets.Where(t => t.DeploymentId == deployment.Id && (t.Status == TargetStatus.WaitingOffline || t.Status == TargetStatus.WaitingWindow))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TargetStatus.Cancelled).SetProperty(t => t.Error, reason).SetProperty(t => t.FinishedUtc, DateTime.UtcNow));
    }

    private async Task RunTargetAsync(Deployment deployment, DeploymentTarget target, BuiltJob built, bool checkOnline, Dictionary<int, DateTime?> closed, SemaphoreSlim? own, CancellationToken ct)
    {
        var status = TargetStatus.Failed;
        string? error = null;
        List<StepResult> steps = [];
        var started = false;
        var ownTaken = false;
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
            if (own is not null)
            {
                await own.WaitAsync(ct);
                ownTaken = true;
            }
            await _slots.EnterAsync(ct);
            started = true;
            // Erst jetzt prüfen: die Grenze kann erreicht worden sein, während dieses Ziel auf einen freien Platz gewartet hat.
            // Eine Fortsetzung nach einem Neustart läuft trotzdem zu Ende.
            if (resumeFrom == 0 && (_stopped.TryGetValue(deployment.Id, out var stopReason) || (stopReason = deployment.StopReason) is not null))
            {
                status = TargetStatus.Cancelled;
                error = stopReason;
                return;
            }
            if (checkOnline && !await IsReachableAsync(target.ComputerName, ct))
            {
                status = TargetStatus.WaitingOffline;
                error = await WakeAsync(deployment, target, ct) ? WokenText : WaitingText;
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
            _live.TryRemove(target.Id, out _);
            try
            {
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
            if (status == TargetStatus.Failed)
                await CheckFailureLimitAsync(deployment);
            }
            finally
            {
                // Erst nach der Prüfung freigeben, damit das nächste Ziel die erreichte Grenze schon sieht – auch wenn das Speichern scheitert.
                if (started)
                    _slots.Exit();
                if (ownTaken)
                    own!.Release();
            }
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
