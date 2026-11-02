using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.NetworkInformation;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Gemeinsame LDAP-Anmeldung für das Verzeichnis und die DNS-Zonen: Negotiate, unter Windows signiert und verschlüsselt.</summary>
internal static class LdapConnector
{
    /// <summary>Der angegebene Domänencontroller oder die Domäne, in der der Server Mitglied ist.</summary>
    /// <exception cref="RemoteException">Keins von beidem.</exception>
    public static string Server(string? server)
    {
        var resolved = string.IsNullOrWhiteSpace(server) ? IPGlobalProperties.GetIPGlobalProperties().DomainName : server.Trim();
        return string.IsNullOrWhiteSpace(resolved)
            ? throw new RemoteException("No domain controller specified, and the server is not a member of a domain.")
            : resolved;
    }

    public static LdapConnection Connect(string server, NetworkCredential? credential)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(server));
        try
        {
            connection.AuthType = AuthType.Negotiate;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Timeout = TimeSpan.FromSeconds(30);
            if (OperatingSystem.IsWindows())
            {
                // Schützt die Abfrage auch ohne LDAPS; viele Domänen verlangen das inzwischen.
                connection.SessionOptions.Signing = true;
                connection.SessionOptions.Sealing = true;
            }
            if (credential is not null)
                connection.Credential = credential;
            connection.Bind();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Ein Attribut des RootDSE (defaultNamingContext, rootDomainNamingContext …); null, wenn es fehlt.</summary>
    public static string? RootValue(LdapConnection connection, string attribute)
    {
        var response = (SearchResponse)connection.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, attribute));
        return response.Entries.Count > 0 && response.Entries[0].Attributes[attribute] is { Count: > 0 } values && values[0] is string { Length: > 0 } text ? text : null;
    }
}
