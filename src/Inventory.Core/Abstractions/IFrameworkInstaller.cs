namespace Inventory.Core.Abstractions;

/// <param name="RebootRequired">Installiert, wirksam aber erst nach einem Neustart des Rechners.</param>
/// <param name="Message">Ergebnis in Worten, für die Oberfläche und das Audit-Protokoll.</param>
public sealed record FrameworkInstallResult(bool Success, bool RebootRequired, string Message);

/// <summary>
/// Installiert .NET Framework 4.8 auf einem Rechner, ohne den Runner zu brauchen: gerade dort, wo er mangels Framework
/// nicht startet (Windows 7, Server 2008 R2).
/// </summary>
public interface IFrameworkInstaller
{
    /// <param name="installer">Der Offline-Installer von Microsoft (ndp48-x86-x64-allos-enu.exe), Signatur bereits geprüft.</param>
    /// <exception cref="RemoteException">Der Rechner ist nicht erreichbar oder der Installer ließ sich nicht starten.</exception>
    Task<FrameworkInstallResult> InstallAsync(RemoteTarget target, string installer, CancellationToken ct);
}

/// <summary>Beschafft den Offline-Installer von .NET Framework 4.8 und prüft seine Signatur.</summary>
public interface IFrameworkSource
{
    /// <returns>Pfad zum geprüften Installer.</returns>
    /// <exception cref="InvalidOperationException">Kein Download möglich oder Signatur ungültig; die Meldung sagt, was zu tun ist.</exception>
    Task<string> GetInstallerAsync(CancellationToken ct);
}
