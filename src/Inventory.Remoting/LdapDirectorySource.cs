using System.DirectoryServices.Protocols;
using System.Net.NetworkInformation;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

public sealed class LdapDirectorySource : IDirectorySource
{
    // Aktivierte Computerkonten: Bit 2 (ACCOUNTDISABLE) in userAccountControl darf nicht gesetzt sein.
    private const string Filter = "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";
    private const int PageSize = 500;
    private static readonly string[] Attributes = ["name", "dNSHostName", "operatingSystem", "description"];

    public Task<List<DirectoryComputer>> GetComputersAsync(DirectoryQuery query, CancellationToken ct = default) =>
        Task.Run(() => Search(query, ct), ct);

    private static List<DirectoryComputer> Search(DirectoryQuery query, CancellationToken ct)
    {
        var server = string.IsNullOrWhiteSpace(query.Server) ? IPGlobalProperties.GetIPGlobalProperties().DomainName : query.Server.Trim();
        if (string.IsNullOrWhiteSpace(server))
            throw new RemoteException("Kein Domänencontroller angegeben, und der Server gehört keiner Domäne an.");

        try
        {
            using var connection = new LdapConnection(new LdapDirectoryIdentifier(server));
            connection.AuthType = AuthType.Negotiate;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Timeout = TimeSpan.FromSeconds(30);
            if (OperatingSystem.IsWindows())
            {
                // Schützt die Abfrage auch ohne LDAPS; viele Domänen verlangen das inzwischen.
                connection.SessionOptions.Signing = true;
                connection.SessionOptions.Sealing = true;
            }
            if (query.Credential is not null)
                connection.Credential = query.Credential;
            connection.Bind();

            var baseDns = query.BaseDns.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
            if (baseDns.Count == 0)
                baseDns.Add(DefaultNamingContext(connection));

            var result = new Dictionary<string, DirectoryComputer>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseDn in baseDns)
            {
                var paging = new PageResultRequestControl(PageSize);
                var request = new SearchRequest(baseDn, Filter, SearchScope.Subtree, Attributes);
                request.Controls.Add(paging);
                do
                {
                    ct.ThrowIfCancellationRequested();
                    var response = (SearchResponse)connection.SendRequest(request);
                    foreach (SearchResultEntry entry in response.Entries)
                    {
                        if (Value(entry, "name") is { } name)
                            result[name] = new DirectoryComputer(name, Value(entry, "dNSHostName"), Value(entry, "operatingSystem"), Value(entry, "description"));
                    }
                    paging.Cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? [];
                } while (paging.Cookie.Length > 0);
            }
            return result.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (LdapException ex)
        {
            throw new RemoteException($"LDAP-Abfrage an {server} fehlgeschlagen: {ex.Message}", ex);
        }
        catch (DirectoryOperationException ex)
        {
            throw new RemoteException($"LDAP-Abfrage an {server} abgelehnt: {ex.Message}", ex);
        }
    }

    private static string DefaultNamingContext(LdapConnection connection)
    {
        var response = (SearchResponse)connection.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));
        return response.Entries.Count > 0 && Value(response.Entries[0], "defaultNamingContext") is { } context
            ? context
            : throw new RemoteException("Die Domäne konnte nicht ermittelt werden; bitte eine Suchwurzel (OU) angeben.");
    }

    private static string? Value(SearchResultEntry entry, string attribute) =>
        entry.Attributes[attribute] is { Count: > 0 } values && values[0] is string text && text.Length > 0 ? text : null;
}
