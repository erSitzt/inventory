using Microsoft.Extensions.Hosting.WindowsServices;

namespace Inventory.Server.Services;

/// <summary>Startet den Windows-Dienst neu, damit ein vorbereiteter Import eingesetzt wird.</summary>
public static class ServiceRestart
{
    /// <summary>Nur als Dienst: in der Konsole startet niemand den Prozess wieder.</summary>
    public static bool Available => OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

    /// <summary>
    /// Beendet den Prozess kurz nach dem Aufruf mit einem Fehlercode, ohne dem Dienststeuerungs-Manager "beendet" zu melden.
    /// Der wertet das als Ausfall und startet den Dienst über die Wiederherstellungsoptionen neu, die das Setup einträgt
    /// (nach 10 Sekunden). Ein normales Beenden würde den Dienst gestoppt lassen, und ein Dienstkonto ohne Administratorrechte
    /// darf den eigenen Dienst nicht neu starten.
    /// </summary>
    public static void Schedule(ILogger logger)
    {
        _ = Task.Run(async () =>
        {
            // Zeit für die Antwort an den Browser.
            await Task.Delay(TimeSpan.FromSeconds(2));
            logger.LogWarning("Restarting to apply the prepared import");
            Environment.Exit(1);
        });
    }
}
