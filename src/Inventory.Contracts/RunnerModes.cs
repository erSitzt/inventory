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
    }
}
