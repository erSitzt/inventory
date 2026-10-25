using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>
/// Installiert .NET Framework 4.8 auf Rechnern, auf denen der Runner deshalb nicht startet. Läuft immer über SMB und den
/// Dienststeuer-Manager, ohne Runner und ohne Agent; der Installer kommt einmal von Microsoft und wird auf dem Server zwischengespeichert.
/// </summary>
public sealed class FrameworkInstallService(ComputerService computers, CredentialService credentials, SettingsService settings,
    IFrameworkSource source, IFrameworkInstaller? installer = null, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    /// <summary>false, wenn der Server keinen agentenlosen Zugriff hat (nicht unter Windows).</summary>
    public bool Available => installer is not null;

    /// <summary>Liefert immer ein Ergebnis; Fehler stehen in der Meldung.</summary>
    public async Task<FrameworkInstallResult> InstallAsync(int computerId, CancellationToken ct = default)
    {
        if (installer is null)
            return new FrameworkInstallResult(false, false, L["Installing .NET Framework needs agentless access (SMB), which this server does not have."]);
        var computer = await computers.GetAsync(computerId, ct);
        if (computer is null)
            return new FrameworkInstallResult(false, false, L["Computer {0} not found", computerId]);
        // Wie die Agent-Installation: eine Voraussetzung für den Agenten, deshalb auch bei „nur Agent installieren“ erlaubt.
        if (await settings.GetAgentlessPolicyAsync(ct) == AgentlessPolicy.Never)
            return new FrameworkInstallResult(false, false, L["Agentless access ({0}) is switched off in the settings. Jobs reach this computer only through an agent.", TransportKind.SmbService]);

        string path;
        try
        {
            path = await source.GetInstallerAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            return new FrameworkInstallResult(false, false, ex.Message);
        }
        try
        {
            var credential = await credentials.ResolveAsync(computerId, ct);
            return await installer.InstallAsync(new RemoteTarget(computer.Address, credential), path, ct);
        }
        catch (RemoteException ex)
        {
            return new FrameworkInstallResult(false, false, ex.Message);
        }
    }
}
