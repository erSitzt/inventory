using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Inventory.Core;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting.Devices;

/// <summary>Prüft eine Adresse ohne Anmeldung: Ping, die Standardports aus <see cref="DevicePorts"/>, Banner, Weboberfläche, SNMP.</summary>
public sealed partial class DeviceProbe : IDeviceProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    private const int MaxHttpBytes = 64 * 1024;

    public async Task<DeviceObservation> ProbeAsync(IPAddress address, IReadOnlyList<string> communities, CancellationToken ct = default)
    {
        var observation = new DeviceObservation { IpAddress = address.ToString() };
        var ping = PingAsync(address);
        var snmp = communities.Count > 0 ? SnmpCollector.CollectAsync(address, communities, ct) : Task.FromResult<SnmpFacts?>(null);
        var open = await Task.WhenAll(DevicePorts.Services.Keys.Select(async port => (port, open: await CanConnectAsync(address, port, ct))));
        observation.OpenPorts = [.. open.Where(p => p.open).Select(p => p.port)];
        observation.PingReply = await ping;
        observation.Snmp = await snmp;
        if (!observation.Responded)
            return observation;

        var reverse = ReverseLookupAsync(address, ct);
        var ports = observation.OpenPorts;
        if (ports.Contains(22))
            observation.SshBanner = await ReadLineAsync(address, 22, ct);
        foreach (var port in new[] { 21, 25, 23 }.Where(ports.Contains))
        {
            if ((observation.Banner = await ReadLineAsync(address, port, ct)) is not null)
                break;
        }
        if (ports.Contains(445))
        {
            var identity = await SmbNameQuery.QueryAsync(address, ReadTimeout, ct);
            observation.WindowsName = identity?.NetbiosName ?? identity?.DnsName?.Split('.')[0];
        }
        foreach (var port in DevicePorts.Https.Where(ports.Contains))
        {
            if (await WebAsync(observation, address, port, tls: true, ct))
                break;
        }
        foreach (var port in DevicePorts.Http.Where(ports.Contains))
        {
            if (observation.HttpTitle is not null || await WebAsync(observation, address, port, tls: false, ct))
                break;
        }
        observation.ReverseName = await reverse;
        return observation;
    }

    private const int MaxHops = 16;
    private static readonly byte[] TracePayload = new byte[32];

    public async Task<TraceResult> TraceAsync(IPAddress target, CancellationToken ct = default)
    {
        var hops = new List<string?>();
        using var ping = new Ping();
        for (var ttl = 1; ttl <= MaxHops; ttl++)
        {
            string? hop = null;
            // Router drosseln ihre ICMP-Antworten; ein zweiter Versuch je Hop genügt.
            for (var attempt = 0; attempt < 2 && hop is null; attempt++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(target, TimeSpan.FromSeconds(1), TracePayload, new PingOptions(ttl, true), ct);
                    if (reply.Status == IPStatus.Success)
                        return new TraceResult(hops, true);
                    if (reply.Status is IPStatus.TtlExpired or IPStatus.TimeExceeded && reply.Address is { } address)
                        hop = address.ToString();
                }
                catch (PingException)
                {
                    break;
                }
            }
            hops.Add(hop);
            // Vier stumme Hops in Folge: weiter kommt nichts mehr zurück.
            if (hops.Count >= 4 && hops.TakeLast(4).All(h => h is null))
                break;
        }
        while (hops.Count > 0 && hops[^1] is null)
            hops.RemoveAt(hops.Count - 1);
        return new TraceResult(hops, false);
    }

    public IReadOnlyDictionary<string, string> LocalNeighbors()
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsNeighbors() : LinuxNeighbors();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static async Task<bool> PingAsync(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            return (await ping.SendPingAsync(address, 800)).Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        using var client = new TcpClient(address.AddressFamily);
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Die erste Zeile, die der Dienst von sich aus schickt (SSH, FTP, SMTP, Telnet); Steuerzeichen entfallen.</summary>
    private static async Task<string?> ReadLineAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ReadTimeout);
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, limit.Token);
            var buffer = new byte[512];
            var read = await client.GetStream().ReadAsync(buffer, limit.Token);
            // Telnet beginnt oft mit Optionsverhandlung (Bytes ab 0xFF); nur druckbare Zeichen der ersten Zeile zählen.
            var text = Encoding.ASCII.GetString([.. buffer.Take(read).TakeWhile(b => b != '\n').Where(b => b is >= 0x20 and < 0x7F)]).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Holt die Startseite: Server-Kopfzeile, Seitentitel, Anmelde-Realm und bei TLS das Zertifikat. Folgt Weiterleitungen auf demselben Gerät.</summary>
    private static async Task<bool> WebAsync(DeviceObservation observation, IPAddress address, int port, bool tls, CancellationToken ct)
    {
        X509Certificate2? certificate = null;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = ConnectTimeout,
            // Geräte haben fast immer selbstsignierte Zertifikate; hier wird nur gelesen, nichts gesendet.
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    certificate ??= cert is null ? null : new X509Certificate2(cert);
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler) { Timeout = ReadTimeout };
        var uri = new Uri($"{(tls ? "https" : "http")}://{address}:{port}/");
        try
        {
            for (var hop = 0; hop < 3; hop++)
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                observation.HttpServer ??= response.Headers.Server.ToString() is { Length: > 0 } server ? server : null;
                if (response.Headers.WwwAuthenticate.FirstOrDefault()?.Parameter is { } parameter && RealmPattern().Match(parameter) is { Success: true } realm)
                    observation.HttpRealm ??= realm.Groups[1].Value;
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next.Host != uri.Host)
                        break;
                    uri = next;
                    continue;
                }
                var body = await ReadBodyAsync(response, ct);
                if (TitlePattern().Match(body) is { Success: true } title && WebUtility.HtmlDecode(title.Groups[1].Value).Trim() is { Length: > 0 } text)
                    observation.HttpTitle = Shorten(Regex.Replace(text, @"\s+", " "), 200);
                break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            ct.ThrowIfCancellationRequested();
        }
        if (certificate is not null)
        {
            observation.TlsSubject ??= Shorten(certificate.Subject, 300);
            observation.TlsIssuer ??= Shorten(certificate.Issuer, 300);
            certificate.Dispose();
        }
        return observation.HttpTitle is not null;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxHttpBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static async Task<string?> ReverseLookupAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), ct).WaitAsync(ReadTimeout, ct);
            return IPAddress.TryParse(entry.HostName, out _) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return null;
        }
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length];

    private static Dictionary<string, string> LinuxNeighbors()
    {
        var result = new Dictionary<string, string>();
        const string path = "/proc/net/arp";
        if (!File.Exists(path))
            return result;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && WakeOnLan.NormalizeMac(parts[3]) is { } mac)
                result[parts[0]] = mac;
        }
        return result;
    }

    private static Dictionary<string, string> WindowsNeighbors()
    {
        var result = new Dictionary<string, string>();
        var size = 0;
        _ = GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
            return result;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0)
                return result;
            var count = Marshal.ReadInt32(buffer);
            // MIB_IPNETROW: Index, PhysAddrLen, PhysAddr[8], Addr, Type = 24 Bytes.
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var length = Marshal.ReadInt32(row, 4);
                var type = Marshal.ReadInt32(row, 20);
                // 2 = ungültig.
                if (length != 6 || type == 2)
                    continue;
                var mac = new byte[6];
                Marshal.Copy(row + 8, mac, 0, 6);
                var ip = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(ip, Marshal.ReadInt32(row, 16));
                if (WakeOnLan.NormalizeMac(Convert.ToHexString(mac)) is { } normalized)
                    result[new IPAddress(ip).ToString()] = normalized;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"realm=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex RealmPattern();
}
