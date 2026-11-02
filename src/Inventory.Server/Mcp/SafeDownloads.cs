using System.Net;
using System.Net.Sockets;
using Inventory.Core;

namespace Inventory.Server.Mcp;

/// <summary>
/// Downloads für Pakete von KI-Assistenten, ohne dass der Server zum Werkzeug für Zugriffe ins eigene Netz wird (SSRF):
/// jede Verbindung – auch nach einer Weiterleitung – prüft die tatsächlich angewählte Adresse. Loopback, Link-Local
/// (etwa Cloud-Metadaten unter 169.254.169.254), Multicast und unbestimmte Adressen sind immer gesperrt; private Netze
/// nur für Hosts, die ein Administrator ausdrücklich erlaubt hat.
/// </summary>
public static class SafeDownloads
{
    /// <summary>Hosts (Namen, Adressen oder CIDR-Netze) im internen Netz, z. B. der Artefakt-Server der Builds.</summary>
    public static HttpClient Create(Func<IReadOnlyList<string>> allowedInternal) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = allowedInternal();
            var target = addresses.FirstOrDefault(a => Allowed(host, a, allowed))
                ?? throw new HttpRequestException($"{host} resolves to an address that downloads may not reach ({string.Join(", ", addresses.Select(a => a.ToString()))}).");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Ob eine Verbindung zu dieser Adresse erlaubt ist.</summary>
    public static bool Allowed(string host, IPAddress address, IReadOnlyList<string> allowedInternal)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 || (b[0] == 169 && b[1] == 254) || b[0] >= 224)
                return false;
            var internalNet = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return !internalNet || IsAllowedInternal(host, address.ToString(), allowedInternal);
        }
        // IPv6: eindeutige lokale Adressen (fc00::/7) wie private IPv4-Netze, Site-Local gesperrt.
        if (address.IsIPv6SiteLocal)
            return false;
        var first = address.GetAddressBytes()[0];
        return (first & 0xFE) != 0xFC || IsAllowedInternal(host, address.ToString(), allowedInternal);
    }

    private static bool IsAllowedInternal(string host, string address, IReadOnlyList<string> allowed) =>
        allowed.Any(entry => entry.Equals(host, StringComparison.OrdinalIgnoreCase) || entry == address || (entry.Contains('/') && IpRanges.Contains(entry, address)));

    /// <summary>Für das Protokoll: ohne Abfrage und Anmeldedaten, die Download-Links oft als Token tragen.</summary>
    public static string Redact(Uri uri) => $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.AbsolutePath}";
}
