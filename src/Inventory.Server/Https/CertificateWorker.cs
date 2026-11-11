namespace Inventory.Server.Https;

/// <summary>
/// Prüft stündlich das HTTPS-Zertifikat: Let's Encrypt erneuern, wenn fällig, das selbstsignierte erneuern, Erneuerungen aus
/// Speicher oder Datei übernehmen, vor dem Ablauf warnen. Der erste Durchlauf kommt kurz nach dem Start, damit ein fehlendes
/// Let's-Encrypt-Zertifikat nicht eine Stunde auf sich warten lässt.
/// </summary>
public sealed class CertificateWorker(HttpsEndpoint https, ServerCertificateService certificates, AcmeService acme, ILogger<CertificateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, stoppingToken);
            using var timer = new PeriodicTimer(Tick);
            do
            {
                if (!https.Active)
                    continue;
                try
                {
                    await acme.RenewIfDueAsync(stoppingToken);
                    await certificates.RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error while checking the HTTPS certificate");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
