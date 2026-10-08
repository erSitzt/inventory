namespace Inventory.Contracts
{
    /// <summary>Kommandozeilen-Modi des Runners; von Server (Transporte) und Runner gemeinsam genutzt.</summary>
    public static class RunnerModes
    {
        public const string Service = "--service";
        public const string Console = "--console";
        public const string Version = "--version";
    }
}
