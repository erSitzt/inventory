namespace Inventory.Server.Https;

/// <summary>Prüft stündlich das HTTPS-Zertifikat: erneuern, Erneuerungen übernehmen, vor dem Ablauf warnen.</summary>
public sealed class CertificateWorker(HttpsEndpoint https, ServerCertificateService certificates, ILogger<CertificateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!https.Active)
                    continue;
                try
                {
                    await certificates.RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error while checking the HTTPS certificate");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
