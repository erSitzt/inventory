using System.Net;

namespace Inventory.Core.Abstractions;

/// <param name="Enabled">false, wenn das Computerkonto im Verzeichnis deaktiviert ist.</param>
public sealed record DirectoryComputer(string Name, string? DnsHostName, string? OperatingSystem, string? Description, bool Enabled = true);

/// <param name="Server">Domänencontroller oder DNS-Name der Domäne; leer = Domäne des Servers.</param>
/// <param name="BaseDns">Suchwurzeln (OUs); leer = gesamte Domäne.</param>
public sealed record DirectoryQuery(string? Server, IReadOnlyList<string> BaseDns, NetworkCredential? Credential);

/// <summary>Liest die Computerkonten aus dem Active Directory, aktivierte wie deaktivierte.</summary>
public interface IDirectorySource
{
    /// <exception cref="RemoteException">Das Verzeichnis ist nicht erreichbar oder lehnt die Anfrage ab.</exception>
    Task<List<DirectoryComputer>> GetComputersAsync(DirectoryQuery query, CancellationToken ct = default);
}
