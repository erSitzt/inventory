using System.Net;

namespace Inventory.Core.Abstractions;

/// <summary>Leichte Prüfung einzelner Adressen für die IP-Verwaltung: Ping und DNS über den Resolver des Servers.</summary>
public interface IAddressProbe
{
    Task<bool> PingAsync(IPAddress address, CancellationToken ct = default);

    /// <summary>Name aus dem PTR-Eintrag; null, wenn es keinen gibt.</summary>
    Task<string?> ReverseAsync(IPAddress address, CancellationToken ct = default);

    /// <summary>IPv4-Adressen eines Namens; leer, wenn er nicht auflöst.</summary>
    Task<IReadOnlyList<string>> ForwardAsync(string name, CancellationToken ct = default);

    /// <summary>MAC-Adressen aus der ARP-Tabelle des Servers; Schlüssel = IP.</summary>
    IReadOnlyDictionary<string, string> LocalNeighbors();
}
