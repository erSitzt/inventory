using System.Net;
using System.Net.Sockets;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Löst den Namen auf und prüft die für die Transporte relevanten Ports (SMB, WinRM).</summary>
public sealed class TcpHostProbe : IHostProbe
{
    private static readonly int[] Ports = [445, 5985];
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public async Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException ex)
        {
            return new ProbeResult(false, null, $"Namensauflösung fehlgeschlagen: {ex.SocketErrorCode}");
        }

        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (address is null)
            return new ProbeResult(false, null, "Keine Adresse gefunden");

        foreach (var port in Ports)
        {
            if (await CanConnectAsync(address, port, ct))
                return new ProbeResult(true, address.ToString());
        }
        return new ProbeResult(false, address.ToString(), $"Ports {string.Join("/", Ports)} nicht erreichbar");
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
