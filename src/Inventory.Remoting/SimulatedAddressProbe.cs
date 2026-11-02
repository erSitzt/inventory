using System.Net;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Beispielnetz für den Simulationsmodus, passend zu <see cref="SimulatedDnsZoneSource"/>: in jedem Netz antworten .1–.3, .10–.11,
/// .20–.39 und jede siebte Adresse ab .100; .150–.199 ist ein DHCP-Bereich mit wechselnden Clients.
/// </summary>
public sealed class SimulatedAddressProbe : IAddressProbe
{
    public async Task<bool> PingAsync(IPAddress address, CancellationToken ct = default)
    {
        await Task.Delay(5, ct);
        return Answers(address.GetAddressBytes()[3]);
    }

    internal static bool Answers(int octet) =>
        octet is >= 1 and <= 3 or 10 or 11 or >= 20 and <= 39 || (octet is >= 100 and < 150 && octet % 7 == 0) || (octet is >= 150 and < 200 && octet % 3 == 0);

    public Task<string?> ReverseAsync(IPAddress address, CancellationToken ct = default) =>
        Task.FromResult(SimulatedDnsZoneSource.PointerName(address.ToString()));

    public Task<IReadOnlyList<string>> ForwardAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(SimulatedDnsZoneSource.Resolve(name));

    public IReadOnlyDictionary<string, string> LocalNeighbors() => new Dictionary<string, string>();
}
