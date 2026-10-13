using Inventory.Data.Services;

namespace Inventory.Server.Services;

/// <summary>Takt für alles Zeitgesteuerte: fällige Zeitpläne, Nachholen von Offline-Rechnern, Heartbeat, AD-Synchronisation, Datensicherung.</summary>
public sealed class SchedulerWorker(ScheduleService schedules, DeploymentService deployments, AdSyncService adSync, HeartbeatService heartbeat, BackupService backups, InventoryStore inventory, AuditService audit, SettingsService settings, UpdateService updates, CatalogService catalog, TrendService trends, AgentService agents, Inventory.Core.Abstractions.IAlertSink alertSink, ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(730);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastRetry = DateTime.MinValue;
        var lastBackupAlert = DateTime.MinValue;
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await schedules.RunDueAsync(stoppingToken);
                await deployments.ResumeRebootedAsync(stoppingToken);
                await deployments.ReleasePilotsAsync(stoppingToken);
                // Geweckte Rechner werden nachgeholt, sobald sie Zeit zum Hochfahren hatten.
                if (DateTime.UtcNow - lastRetry >= RetryInterval || deployments.TakeWakeRecheck(DateTime.UtcNow))
                {
                    lastRetry = DateTime.UtcNow;
                    await deployments.RetryWaitingAsync(stoppingToken);
                }
                await heartbeat.RunIfDueAsync(stoppingToken);
                await adSync.SyncIfDueAsync(stoppingToken);
                await updates.CheckAsync(force: false, stoppingToken);
                await updates.ReportPreviousAsync(startup: false, stoppingToken);
                await updates.ApplyIfDueAsync(stoppingToken);
                await catalog.UpdateDueAsync(stoppingToken);
                if (await agents.CloseExpiredPublicDownloadAsync(stoppingToken))
                    await audit.LogAsync(Inventory.Core.Model.AuditActor.System("(automatic)"), "Setting changed", "Download page without sign-in: closed after its period", stoppingToken);
                // Einmal täglich die Kennzahlen der Übersicht festhalten (der erste Takt des Tages).
                await trends.TakeIfDueAsync(stoppingToken);
                var backedUp = false;
                try
                {
                    backedUp = await backups.BackupIfDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Die Sicherung wird bei jedem Takt erneut versucht; gemeldet wird sie höchstens stündlich.
                    logger.LogError(ex, "Backup failed");
                    if (DateTime.UtcNow - lastBackupAlert >= TimeSpan.FromHours(1))
                    {
                        lastBackupAlert = DateTime.UtcNow;
                        alertSink.Raise(new(Inventory.Core.Model.AlertKind.BackupFailed, "Database backup failed", ex.Message, "settings"));
                    }
                }
                // Einmal täglich, zusammen mit der Sicherung: Verlauf auf die Aufbewahrungsfrist kürzen.
                if (backedUp)
                {
                    await inventory.CleanupChangesAsync(HistoryRetention, stoppingToken);
                    await audit.CleanupAsync(await settings.GetAuditRetentionDaysAsync(stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Ein Fehler in einem Durchlauf darf den Takt nicht beenden.
                logger.LogError(ex, "Error in the scheduler");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result, TaskScheduler.Default));
    }
}
