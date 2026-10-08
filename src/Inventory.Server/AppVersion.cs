using System.Reflection;

namespace Inventory.Server;

public static class AppVersion
{
    /// <summary>Version dieses Builds, z. B. "1.2.0" oder "0.0.0-dev" für lokal gebaute Stände.</summary>
    public static string Current { get; } =
        // Hinter dem Plus hängt der Compiler den Commit an; für Anzeige und Vergleich zählt nur der Teil davor.
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev").Split('+')[0];
}
