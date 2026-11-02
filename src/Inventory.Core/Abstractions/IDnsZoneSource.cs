using System.Net;

namespace Inventory.Core.Abstractions;

/// <param name="Server">Domänencontroller; null = die Domäne des Servers.</param>
/// <param name="Credential">null = das Konto des Dienstes.</param>
public sealed record DnsZoneQuery(string? Server, NetworkCredential? Credential);

/// <summary>Ein A- oder PTR-Eintrag aus einer AD-integrierten Zone.</summary>
/// <param name="Name">Vollständiger Hostname (A: der Eintrag selbst, PTR: das Ziel), ohne abschließenden Punkt.</param>
/// <param name="RefreshedUtc">Zeitstempel der dynamischen Registrierung; null = statischer Eintrag.</param>
public sealed record DnsZoneRecord(string Name, string Address, DateTime? RefreshedUtc);

/// <param name="Zones">Anzahl der gelesenen Zonen.</param>
public sealed record DnsZoneData(IReadOnlyList<DnsZoneRecord> AddressRecords, IReadOnlyList<DnsZoneRecord> PointerRecords, int Zones);

/// <summary>Liest die A- und PTR-Einträge der DNS-Zonen, die im Active Directory gespeichert sind (nur lesend).</summary>
public interface IDnsZoneSource
{
    /// <exception cref="RemoteException">Das Verzeichnis ist nicht erreichbar oder lehnt die Anfrage ab.</exception>
    Task<DnsZoneData> ReadAsync(DnsZoneQuery query, CancellationToken ct = default);
}
