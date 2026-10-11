using System.Net;

namespace Inventory.Core.Abstractions;

/// <param name="Enabled">false, wenn das Computerkonto im Verzeichnis deaktiviert ist.</param>
/// <param name="DistinguishedName">Vollständiger Name des Kontos, z. B. CN=PC-01,OU=Laptops,DC=corp,DC=example.</param>
/// <param name="Groups">Gruppen, in denen das Konto direkt Mitglied ist (ohne die primäre Gruppe und ohne verschachtelte).</param>
/// <param name="LastLogonUtc">Letzte Anmeldung laut lastLogonTimestamp; das Verzeichnis gleicht den Wert nur etwa alle zwei Wochen ab.</param>
public sealed record DirectoryComputer(string Name, string? DnsHostName, string? OperatingSystem, string? Description, bool Enabled = true,
    string? DistinguishedName = null, IReadOnlyList<string>? Groups = null, DateTime? LastLogonUtc = null);

/// <param name="Server">Domänencontroller oder DNS-Name der Domäne; leer = Domäne des Servers.</param>
/// <param name="BaseDns">Suchwurzeln (OUs); leer = gesamte Domäne.</param>
public sealed record DirectoryQuery(string? Server, IReadOnlyList<string> BaseDns, NetworkCredential? Credential);

/// <summary>Liest die Computerkonten aus dem Active Directory, aktivierte wie deaktivierte.</summary>
public interface IDirectorySource
{
    /// <exception cref="RemoteException">Das Verzeichnis ist nicht erreichbar oder lehnt die Anfrage ab.</exception>
    Task<List<DirectoryComputer>> GetComputersAsync(DirectoryQuery query, CancellationToken ct = default);
}
