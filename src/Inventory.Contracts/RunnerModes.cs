namespace Inventory.Contracts
{
    /// <summary>Kommandozeilen-Modi des Runners; von Server (Transporte) und Runner gemeinsam genutzt.</summary>
    public static class RunnerModes
    {
        public const string Service = "--service";
        public const string Console = "--console";
        public const string Version = "--version";
        /// <summary>Dauerbetrieb als Windows-Dienst.</summary>
        public const string Agent = "--agent";
        /// <summary>Agent im Vordergrund, zur Fehlersuche.</summary>
        public const string AgentConsole = "--agent-console";
        /// <summary>Agent aus dem Ordner des Programms heraus einrichten (Agent-Setup des Servers).</summary>
        public const string InstallAgent = "--install-agent";
        /// <summary>Nach <see cref="InstallAgent"/>: auch einen vorhandenen Agenten mit den Dateien des Setups überschreiben.</summary>
        public const string Force = "--force";
        public const string UninstallAgent = "--uninstall-agent";
    }
}
