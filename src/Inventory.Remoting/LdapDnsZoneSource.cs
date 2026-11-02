using System.DirectoryServices.Protocols;
using System.Text;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Liest A- und PTR-Einträge aus den AD-integrierten DNS-Zonen: den Anwendungspartitionen DomainDnsZones und ForestDnsZones
/// und dem alten Speicherort unter CN=System. Nur lesend; gelöschte (tombstoned) Einträge werden übergangen.
/// </summary>
public sealed class LdapDnsZoneSource : IDnsZoneSource
{
    private const int PageSize = 1000;
    private static readonly string[] Attributes = ["name", "dnsRecord", "dNSTombstoned"];

    public Task<DnsZoneData> ReadAsync(DnsZoneQuery query, CancellationToken ct = default) => Task.Run(() => Read(query, ct), ct);

    private static DnsZoneData Read(DnsZoneQuery query, CancellationToken ct)
    {
        var server = LdapConnector.Server(query.Server);
        try
        {
            using var connection = LdapConnector.Connect(server, query.Credential);
            var domain = LdapConnector.RootValue(connection, "defaultNamingContext")
                ?? throw new RemoteException("The domain could not be determined.");
            var forest = LdapConnector.RootValue(connection, "rootDomainNamingContext") ?? domain;
            string[] bases = [$"DC=DomainDnsZones,{domain}", $"DC=ForestDnsZones,{forest}", $"CN=MicrosoftDNS,CN=System,{domain}"];

            var a = new List<DnsZoneRecord>();
            var ptr = new List<DnsZoneRecord>();
            var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseDn in bases.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var entry in Search(connection, baseDn, ct))
                {
                    if (Value(entry, "dNSTombstoned") is { } tombstoned && tombstoned.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var rdns = Rdns(entry.DistinguishedName);
                    if (rdns.Count < 2 || Value(entry, "name") is not { } node)
                        continue;
                    var zone = rdns[1];
                    if (zone.StartsWith("_msdcs.", StringComparison.OrdinalIgnoreCase) || zone is "RootDNSServers" or "..TrustAnchors")
                        continue;
                    zones.Add(zone);
                    var reverse = DnsRecordParser.ReverseAddress(node, zone);
                    var name = node == "@" ? zone : $"{node}.{zone}";
                    foreach (var data in entry.Attributes["dnsRecord"]?.GetValues(typeof(byte[])).Cast<byte[]>() ?? [])
                    {
                        if (DnsRecordParser.Parse(data) is not { } record)
                            continue;
                        if (record.Type == DnsRecordParser.TypeA && reverse is null)
                            a.Add(new DnsZoneRecord(name, record.Value, record.RefreshedUtc));
                        else if (record.Type == DnsRecordParser.TypePtr && reverse is not null)
                            ptr.Add(new DnsZoneRecord(record.Value.TrimEnd('.'), reverse, record.RefreshedUtc));
                    }
                }
            }
            return new DnsZoneData(a, ptr, zones.Count);
        }
        catch (LdapException ex)
        {
            throw new RemoteException($"LDAP query to {server} failed: {ex.Message}", ex);
        }
        catch (DirectoryOperationException ex)
        {
            throw new RemoteException($"LDAP query to {server} rejected: {ex.Message}", ex);
        }
    }

    /// <summary>Alle dnsNode-Objekte unter einer Basis; eine fehlende Partition ergibt nichts.</summary>
    private static IEnumerable<SearchResultEntry> Search(LdapConnection connection, string baseDn, CancellationToken ct)
    {
        var paging = new PageResultRequestControl(PageSize);
        var request = new SearchRequest(baseDn, "(objectClass=dnsNode)", SearchScope.Subtree, Attributes);
        request.Controls.Add(paging);
        do
        {
            ct.ThrowIfCancellationRequested();
            SearchResponse response;
            try
            {
                response = (SearchResponse)connection.SendRequest(request);
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                yield break;
            }
            foreach (SearchResultEntry entry in response.Entries)
                yield return entry;
            paging.Cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? [];
        } while (paging.Cookie.Length > 0);
    }

    /// <summary>Die Werte der RDNs eines DN der Reihe nach, mit aufgelösten Escapes ("\," und "\2C").</summary>
    internal static List<string> Rdns(string dn)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var bytes = new List<byte>();
        var inValue = false;
        void Flush()
        {
            if (bytes.Count > 0)
            {
                current.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }
        }
        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];
            if (c == '\\' && i + 1 < dn.Length)
            {
                if (i + 2 < dn.Length && Uri.IsHexDigit(dn[i + 1]) && Uri.IsHexDigit(dn[i + 2]))
                {
                    bytes.Add(Convert.ToByte(dn.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    Flush();
                    current.Append(dn[++i]);
                }
                continue;
            }
            Flush();
            if (c == '=' && !inValue)
            {
                inValue = true;
                current.Clear();
            }
            else if (c == ',')
            {
                result.Add(current.ToString());
                current.Clear();
                inValue = false;
            }
            else if (inValue)
            {
                current.Append(c);
            }
        }
        Flush();
        if (inValue)
            result.Add(current.ToString());
        return result;
    }

    private static string? Value(SearchResultEntry entry, string attribute) =>
        entry.Attributes[attribute] is { Count: > 0 } values && values[0] is string text && text.Length > 0 ? text : null;
}
