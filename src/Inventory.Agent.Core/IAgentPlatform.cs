using System;
using Inventory.Contracts;

namespace Inventory.Agent
{
    /// <summary>Was der <see cref="AgentLoop"/> von der Plattform braucht, auf der er läuft.</summary>
    public interface IAgentPlatform
    {
        /// <summary>Der Ordner der Agent-Dateien; dort liegen auch Konfiguration, Schlüssel, Jobs und abgelegte Ergebnisse.</summary>
        string Directory { get; }

        string Version { get; }

        /// <summary><see cref="RunnerRuntimes"/>: für welche Laufzeit der Agent Updates holt.</summary>
        string Runtime { get; }

        /// <summary>Der Rechnername, unter dem sich der Agent anmeldet und der Server ihm Jobs zuordnet.</summary>
        string HostName { get; }

        /// <summary>Rechnername mit DNS-Suffix; null, wenn der Rechner keines hat.</summary>
        string? DnsName();

        /// <summary>Ein Job, dessen Manifest und Dateien schon im Job-Verzeichnis liegen.</summary>
        IAgentJob CreateJob(string jobDir);

        /// <summary>Startzeit des Betriebssystems; daran erkennt der Agent, ob ein verlangter Neustart stattgefunden hat.</summary>
        DateTime BootTimeUtc();

        /// <summary>Nach dem Herunterladen einer neuen Agent-Datei, bevor sie die laufende ersetzt.</summary>
        void PrepareUpdatedFile(string path);
    }

    /// <summary>Ein laufender Job: Fortschritt schreibt er ins Job-Verzeichnis, das Ergebnis gibt er zurück.</summary>
    public interface IAgentJob
    {
        JobResult Run();

        /// <summary>Bricht den laufenden Schritt ab; die restlichen werden übersprungen.</summary>
        void Cancel();
    }
}
