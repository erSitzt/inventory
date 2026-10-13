using System.Net;

namespace Inventory.Core.Abstractions;

/// <summary>Schickt Wake-on-LAN-Pakete ins Netz.</summary>
public interface IWakeSender
{
    /// <summary>
    /// Schickt das Paket an die Adressen und zusätzlich als Broadcast in jedes Netz, an das der Server selbst angeschlossen ist.
    /// </summary>
    /// <returns>Anzahl der abgeschickten Pakete; 0 heißt, dass kein Weg ins Netz offen war.</returns>
    Task<int> SendAsync(byte[] packet, IReadOnlyCollection<IPAddress> addresses, CancellationToken ct = default);
}
