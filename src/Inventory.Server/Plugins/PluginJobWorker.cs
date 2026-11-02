using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Plugins;

namespace Inventory.Server.Plugins;

/// <summary>
/// Führt die regelmäßigen Aufträge der Plugins aus. Jeder Auftrag läuft für sich (nie zweimal gleichzeitig) und mit eigener
/// Fehlerbehandlung; wann er zuletzt lief, steht in den Einstellungen und überdauert so einen Neustart.
/// </summary>
public sealed class PluginJobWorker(PluginInstances instances, PluginCatalog catalog, SettingsService settings, IAlertSink alerts, ILogger<PluginJobWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!catalog.Loaded.Any())
            return;
        // Je Auftrag erzeugt (PluginInstances): einer, der sich nicht erzeugen lässt, hält die anderen nicht auf.
        var jobs = new List<(IPeriodicJob Job, PluginInfo Owner, PluginJobStatus Status)>();
        foreach (var (job, owner) in instances.Jobs)
        {
            try
            {
                var status = new PluginJobStatus { Name = job.Name, Interval = job.Interval };
                status.LastRunUtc = DateTime.TryParse(await settings.GetAsync(Key(owner, job), stoppingToken), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) ? last : null;
                owner.Jobs.Add(status);
                jobs.Add((job, owner, status));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Plugin {Plugin}: a job could not be prepared", owner.DisplayName);
                owner.Error = $"A job could not be prepared: {ex.Message}";
            }
        }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            var now = DateTime.UtcNow;
            foreach (var (job, owner, status) in jobs)
            {
                if (status.Running || (status.LastRunUtc is { } last && now - last < job.Interval))
                    continue;
                status.Running = true;
                _ = Task.Run(() => RunAsync(job, owner, status, stoppingToken), CancellationToken.None);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result, TaskScheduler.Default));
    }

    private async Task RunAsync(IPeriodicJob job, PluginInfo owner, PluginJobStatus status, CancellationToken ct)
    {
        try
        {
            await job.RunAsync(ct);
            status.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plugin job {Plugin}/{Job} failed", owner.DisplayName, job.Name);
            status.LastError = ex.Message;
            alerts.Raise(new AlertEvent(AlertKind.PluginFailed, $"Plugin job failed: {owner.DisplayName} / {job.Name}", ex.Message, "plugins"));
        }
        finally
        {
            status.LastRunUtc = DateTime.UtcNow;
            status.Running = false;
            try
            {
                await settings.SetAsync(Key(owner, job), status.LastRunUtc.Value.ToString("O"), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not store the last run of {Job}", job.Name);
            }
        }
    }

    private static string Key(PluginInfo owner, IPeriodicJob job) => $"Plugins.{owner.Id}.{job.Name}.LastRunUtc";
}
