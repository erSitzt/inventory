using System.Collections.Generic;

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
    }

    /// <summary>Liegt als <see cref="AgentProtocol.ConfigFile"/> neben dem Agenten.</summary>
    public sealed class AgentConfig
    {
        public string ServerUrl { get; set; } = "";
        /// <summary>Gemeinsames Geheimnis für die erste Anmeldung; danach nutzt der Agent seinen eigenen Schlüssel.</summary>
        public string EnrollmentToken { get; set; } = "";
    }

    public sealed class AgentEnrollRequest
    {
        public string EnrollmentToken { get; set; } = "";
        public string HostName { get; set; } = "";
        public string? Version { get; set; }
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
