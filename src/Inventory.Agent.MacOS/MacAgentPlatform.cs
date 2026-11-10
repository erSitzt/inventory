using System.Reflection;
using System.Runtime.InteropServices;
using Inventory.Contracts;

namespace Inventory.Agent.MacOS;

/// <summary>Der Agent unter macOS, gestartet von launchd (LaunchDaemon, läuft als root).</summary>
internal sealed class MacAgentPlatform : IAgentPlatform
{
    public string Directory { get; } = AppContext.BaseDirectory.TrimEnd('/');

    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";

    /// <summary>Die Architektur dieser Datei: Updates holt der Agent für dieselbe.</summary>
    public string Runtime { get; } = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? RunnerRuntimes.MacArm64 : RunnerRuntimes.MacX64;

    /// <summary>Der lokale Name ("Johns-MacBook-Pro"): DNS-tauglich, anders als der Anzeigename mit Leerzeichen und Apostroph.</summary>
    public string HostName { get; } = LocalHostName();

    internal static string LocalHostName() =>
        Shell.Run("/usr/sbin/scutil", "--get", "LocalHostName")?.Trim() is { Length: > 0 } name ? name : Environment.MachineName.Split('.')[0];

    /// <summary>Der Name mit DNS-Suffix, wenn das Netz eines vergibt (nicht das lokale ".local" von Bonjour).</summary>
    public string? DnsName()
    {
        var name = Shell.Run("/bin/hostname")?.Trim().TrimEnd('.');
        return name is not null && name.Contains('.') && !name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    public IAgentJob CreateJob(string jobDir) => new MacJob(jobDir, HostName);

    public DateTime BootTimeUtc() => MacInventoryCollector.BootTimeUtc() ?? DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// Ausführbar machen und ad hoc signieren: auf Apple Silicon startet macOS nur signierte Programme. Die Signatur prüft
    /// Gatekeeper nicht, weil die Datei nicht aus dem Browser kommt (kein Quarantäne-Attribut).
    /// </summary>
    public void PrepareUpdatedFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Shell.Run("/usr/bin/xattr", "-c", path);
        Shell.Run("/usr/bin/codesign", "--force", "--sign", "-", path);
    }

    public void Describe(AgentEnrollRequest request)
    {
        request.Platform = AgentPlatforms.MacOS;
        request.SerialNumber = MacParsers.IoregValue(Shell.Run("/usr/sbin/ioreg", "-rd1", "-c", "IOPlatformExpertDevice"), "IOPlatformSerialNumber");
    }
}
