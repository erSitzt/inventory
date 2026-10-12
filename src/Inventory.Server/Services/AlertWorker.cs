using Inventory.Data.Services;

namespace Inventory.Server.Services;

/// <summary>
/// Wertet die Alarmregeln aus und verschickt die Nachrichten – in einem eigenen Takt, damit ein langsamer Mailserver
/// oder Webhook weder Zeitpläne noch Verteilungen aufhält.
/// </summary>
public sealed class AlertWorker(AlertService alerts, ILogger<AlertWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await alerts.EvaluateAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Ein Fehler in einem Durchlauf darf den Takt nicht beenden.
                    logger.LogError(ex, "Error while evaluating alert rules");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
