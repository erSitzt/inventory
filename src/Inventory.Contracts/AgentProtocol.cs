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
    }

    public sealed class AgentJobOffer
    {
        public JobManifest Manifest { get; set; } = new JobManifest();
        /// <summary>Per Index abrufbar unter jobs/{id}/files/{index}.</summary>
        public List<string> Files { get; set; } = new List<string>();
    }
}
