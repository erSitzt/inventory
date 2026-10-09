using System.Net;
using System.Net.Sockets;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Löst den Namen auf, prüft die für die Transporte relevanten Ports (SMB, WinRM) und stellt fest,
/// welcher Rechner unter der Adresse tatsächlich antwortet.
/// </summary>
public sealed class TcpHostProbe : IHostProbe
{
    private static readonly int[] Ports = [445, 5985];
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(3);

    public async Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException ex)
        {
            return new ProbeResult(false, null, $"Name resolution failed: {ex.SocketErrorCode}");
        }

        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (address is null)
            return new ProbeResult(false, null, "No address found");

        var reverse = ReverseLookupAsync(address, ct);
        foreach (var port in Ports)
        {
            if (!await CanConnectAsync(address, port, ct))
                continue;
            // Der Rechner selbst ist die verlässlichste Quelle für seinen Namen; Reverse-DNS kann genauso veraltet sein wie der Vorwärtseintrag.
            var identity = await SmbNameQuery.QueryAsync(address, IdentityTimeout, ct);
            return new ProbeResult(true, address.ToString(), null, identity?.NetbiosName ?? identity?.DnsName?.Split('.')[0], await reverse);
        }
        return new ProbeResult(false, address.ToString(), $"Ports {string.Join("/", Ports)} unreachable", null, await reverse);
    }

    private static async Task<string?> ReverseLookupAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), ct).WaitAsync(IdentityTimeout, ct);
            // Ohne PTR-Eintrag liefern manche Resolver einfach die Adresse zurück.
            return IPAddress.TryParse(entry.HostName, out _) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return null;
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
}
