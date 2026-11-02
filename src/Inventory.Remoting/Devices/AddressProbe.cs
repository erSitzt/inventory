using System.Net;
using System.Net.Sockets;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting.Devices;

/// <summary>Ping, PTR- und A-Abfrage über den Resolver des Servers, ARP aus der eigenen Tabelle – dieselben Wege wie der Gerätescan.</summary>
public sealed class AddressProbe : IAddressProbe
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    public Task<bool> PingAsync(IPAddress address, CancellationToken ct = default) => DeviceProbe.PingAsync(address);

    public Task<string?> ReverseAsync(IPAddress address, CancellationToken ct = default) => DeviceProbe.ReverseLookupAsync(address, ct);

    public async Task<IReadOnlyList<string>> ForwardAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, ct).WaitAsync(LookupTimeout, ct);
            return [.. addresses.Select(a => a.ToString()).Distinct()];
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return [];
        }
    }

    public IReadOnlyDictionary<string, string> LocalNeighbors() => DeviceProbe.Neighbors();
}
