using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Installiert nichts; für die Entwicklung ohne Zielrechner. Rechner mit "fail" im Namen scheitern.</summary>
public sealed class SimulatedFrameworkInstaller : IFrameworkInstaller, IFrameworkSource
{
    public Task<string> GetInstallerAsync(CancellationToken ct) => Task.FromResult("ndp48-x86-x64-allos-enu.exe");

    public async Task<FrameworkInstallResult> InstallAsync(RemoteTarget target, string installer, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return target.Host.Contains("fail", StringComparison.OrdinalIgnoreCase)
            ? new FrameworkInstallResult(false, false, "The .NET Framework installation failed with exit code 1603.")
            : new FrameworkInstallResult(true, true, ".NET Framework 4.8 installed; it takes effect after a restart of the computer.");
    }
}
