using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inventory.Contracts
{
    /// <summary>Austauschformate zwischen Agent und Server (HTTP, JSON über <see cref="JobJson"/>).</summary>
    public static class AgentProtocol
    {
        public const string KeyHeader = "X-Agent-Key";
        public const string EnrollPath = "api/agent/enroll";
        public const string PollPath = "api/agent/poll";
        public const string JobsPath = "api/agent/jobs";
        public const string ServiceName = "InventoryAgent";
        public const string ConfigFile = "agent.json";
        public const string KeyFile = "agent.key";
        public const string UpdatePath = "api/agent/update/files";
        /// <summary>Enthält den Hash der installierten Runner-Dateien; daran erkennt der Server veraltete Agenten.</summary>
        public const string VersionFile = "agent.version";
        /// <summary>Anfrageparameter von <see cref="UpdatePath"/>: die Laufzeit, für die der Agent Dateien holt (<see cref="RunnerRuntimes"/>).</summary>
        public const string RuntimeQuery = "runtime";
        /// <summary>Lädt den Agenten für eine Laufzeit herunter (Installation auf dem Mac); nur mit gültigem Anmeldetoken.</summary>
        public const string DownloadPath = "api/agent/download";
        /// <summary>Kopfzeile mit dem Anmeldetoken für <see cref="DownloadPath"/>.</summary>
        public const string EnrollmentTokenHeader = "X-Enrollment-Token";
        /// <summary>Die eine Datei des Mac-Agenten.</summary>
        public const string MacAgentFile = "inventory-agent";
        /// <summary>Hier liegt der Mac-Agent mit Konfiguration, Schlüssel und Ablage (nur für root lesbar).</summary>
        public const string MacAgentDirectory = "/Library/Application Support/Inventory Agent";
        /// <summary>Label des LaunchDaemons; die plist liegt unter /Library/LaunchDaemons/&lt;Label&gt;.plist.</summary>
        public const string MacAgentLabel = "inventory.agent";
        /// <summary>Ordner der noch nicht abgelieferten Ergebnisse im Agent-Ordner.</summary>
        public const string PendingFolder = "pending";
        /// <summary>
        /// Antwort-Kopfzeile auf <see cref="PollPath"/>: die HTTPS-Adresse des Servers. Ein Agent, der noch über HTTP (oder eine
        /// andere Adresse) kommt, prüft sie mit <see cref="PingPath"/> und wechselt, wenn sie geht.
        /// </summary>
        public const string HttpsHeader = "X-Inventory-Https";
        /// <summary>Antwort-Kopfzeile auf <see cref="PollPath"/>: Pins (<see cref="KeyPin"/>) des Serverzertifikats, durch Komma getrennt.</summary>
        public const string PinsHeader = "X-Inventory-Pins";
        /// <summary>Antwortet mit 204, wenn der Schlüssel des Agenten gilt; ändert nichts. Zum Prüfen einer neuen Adresse.</summary>
        public const string PingPath = "api/agent/ping";

        /// <summary>
        /// Pin auf den Schlüssel eines Zertifikats: SHA-256 über den öffentlichen Schlüssel, Base64. Bleibt bei einer Erneuerung
        /// mit demselben Schlüssel gleich. Damit vertraut ein Agent dem selbstsignierten Zertifikat des Servers, ohne dass es im
        /// Zertifikatspeicher stehen muss.
        /// </summary>
        public static string KeyPin(X509Certificate2 certificate)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(certificate.PublicKey.EncodedKeyValue.RawData));
        }
    }

    /// <summary>Betriebssystem-Familien der Agenten; Windows-Agenten melden keine (null).</summary>
    public static class AgentPlatforms
    {
        public const string MacOS = "macOS";
    }

    /// <summary>
    /// Der Runner gibt es zweimal: für .NET Framework 4.8 (klein, auf aktuellen Windows-Versionen vorhanden) und eigenständig
    /// mit .NET 10 (groß, für Rechner ohne .NET Framework 4.8).
    /// </summary>
    public static class RunnerRuntimes
    {
        public const string Framework = "net48";
        public const string SelfContained = "net10";
        /// <summary>Der Mac-Agent für Apple Silicon (eine Datei, eigenständig mit .NET 10).</summary>
        public const string MacArm64 = "osx-arm64";
        /// <summary>Der Mac-Agent für Intel-Macs.</summary>
        public const string MacX64 = "osx-x64";
        /// <summary>
        /// Umgebungsvariable, die der Runner für seine Schritte setzt: sein eigener Ordner. Die Agent-Installation übernimmt
        /// daraus genau den Runner, der den Job ausführt.
        /// </summary>
        public const string DirectoryVariable = "INVENTORY_RUNNER_DIR";
    }

    /// <summary>Liegt als <see cref="AgentProtocol.ConfigFile"/> neben dem Agenten.</summary>
    public sealed class AgentConfig
    {
        /// <summary>Die Adresse aus der Installation; bleibt als Rückfall, wenn <see cref="SecureUrl"/> nicht erreichbar ist.</summary>
        public string ServerUrl { get; set; } = "";
        /// <summary>Gemeinsames Geheimnis für die erste Anmeldung; danach nutzt der Agent seinen eigenen Schlüssel.</summary>
        public string EnrollmentToken { get; set; } = "";
        /// <summary>HTTPS-Adresse des Servers, sobald der Agent sie nutzt (vom Server angeboten und geprüft, oder aus der Installation).</summary>
        public string? SecureUrl { get; set; }
        /// <summary>Pins (<see cref="AgentProtocol.KeyPin"/>) des Serverzertifikats: so gilt es auch ohne vertrauenswürdige Kette.</summary>
        public List<string>? CertificatePins { get; set; }
    }

    public sealed class AgentEnrollRequest
    {
        public string EnrollmentToken { get; set; } = "";
        public string HostName { get; set; } = "";
        /// <summary>Voller DNS-Name (Rechnername + primäres DNS-Suffix); leer ohne Suffix und bei älteren Agenten.</summary>
        public string? DnsName { get; set; }
        public string? Version { get; set; }
        /// <summary><see cref="AgentPlatforms"/>; null = Windows (auch alle älteren Agenten).</summary>
        public string? Platform { get; set; }
        /// <summary>Seriennummer des Geräts: wiederfinden, auch wenn sich der Name ändert. Bisher nur vom Mac-Agenten.</summary>
        public string? SerialNumber { get; set; }
    }

    public sealed class AgentEnrollResponse
    {
        public string AgentKey { get; set; } = "";
    }

    public sealed class AgentPollRequest
    {
        public string? Version { get; set; }
        /// <summary>Inhalt von <see cref="AgentProtocol.VersionFile"/>; leer bei Agenten ohne diese Datei.</summary>
        public string? RunnerHash { get; set; }
        /// <summary><see cref="RunnerRuntimes"/>; leer bei älteren Agenten, die alle mit .NET Framework 4.8 laufen.</summary>
        public string? Runtime { get; set; }
    }

    /// <summary>Der Server hat neuere Runner-Dateien: herunterladen, austauschen, neu starten.</summary>
    public sealed class AgentUpdateOffer
    {
        public string Hash { get; set; } = "";
        /// <summary>Dateinamen, per Index abrufbar unter <see cref="AgentProtocol.UpdatePath"/>/{index}.</summary>
        public List<string> Files { get; set; } = new List<string>();
    }

    public sealed class AgentJobOffer
    {
        public JobManifest Manifest { get; set; } = new JobManifest();
        /// <summary>Per Index abrufbar unter jobs/{id}/files/{index}.</summary>
        public List<string> Files { get; set; } = new List<string>();
        /// <summary>Gesetzt = statt eines Jobs zuerst aktualisieren.</summary>
        public AgentUpdateOffer? Update { get; set; }
    }
}
