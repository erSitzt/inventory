using Inventory.Data.Services;

namespace Inventory.Server.Services;

/// <summary>Takt für alles Zeitgesteuerte: fällige Zeitpläne, Nachholen von Offline-Rechnern, AD-Synchronisation, Datensicherung.</summary>
public sealed class SchedulerWorker(ScheduleService schedules, DeploymentService deployments, AdSyncService adSync, BackupService backups, InventoryStore inventory, UpdateService updates, CatalogService catalog, ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(730);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastRetry = DateTime.MinValue;
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await schedules.RunDueAsync(stoppingToken);
                if (DateTime.UtcNow - lastRetry >= RetryInterval)
                {
                    lastRetry = DateTime.UtcNow;
                    await deployments.RetryWaitingAsync(stoppingToken);
                }
                await adSync.SyncIfDueAsync(stoppingToken);
                await updates.CheckAsync(force: false, stoppingToken);
                await updates.ReportPreviousAsync(startup: false, stoppingToken);
                await updates.ApplyIfDueAsync(stoppingToken);
                await catalog.UpdateDueAsync(stoppingToken);
                // Einmal täglich, zusammen mit der Sicherung: Verlauf auf die Aufbewahrungsfrist kürzen.
                if (await backups.BackupIfDueAsync(stoppingToken))
                    await inventory.CleanupChangesAsync(HistoryRetention, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Ein Fehler in einem Durchlauf darf den Takt nicht beenden.
                logger.LogError(ex, "Fehler im Zeitplaner");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result, TaskScheduler.Default));
    }
}
