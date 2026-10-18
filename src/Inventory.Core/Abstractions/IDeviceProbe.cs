namespace Inventory.Core.Abstractions;

/// <summary>Antwort eines Geräts auf SNMP (v1/v2c); nur die Werte, die das Gerät tatsächlich geliefert hat.</summary>
public sealed class SnmpFacts
{
    /// <summary>Die Community, mit der das Gerät geantwortet hat; wird nicht gespeichert.</summary>
    public string? Community { get; set; }
    public int Version { get; set; }
    public string? SysDescr { get; set; }
    public string? SysObjectId { get; set; }
    public string? SysName { get; set; }
    public string? SysContact { get; set; }
    public string? SysLocation { get; set; }
    public long? UptimeSeconds { get; set; }
    /// <summary>ipForwarding: true = das Gerät leitet IP-Pakete weiter (Router, Firewall, Layer-3-Switch).</summary>
    public bool? Forwarding { get; set; }
    /// <summary>Anzahl der Bridge-Ports (BRIDGE-MIB); mehr als null deutet auf einen Switch.</summary>
    public int? BridgePorts { get; set; }
    public int? InterfaceCount { get; set; }
    /// <summary>Modell, Seriennummer und Firmware aus der ENTITY-MIB oder der Drucker-MIB.</summary>
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    public string? Manufacturer { get; set; }
    /// <summary>Das Gerät beantwortet die Drucker-MIB.</summary>
    public bool IsPrinter { get; set; }
    public long? PageCount { get; set; }
    public List<SupplyFacts> Supplies { get; set; } = [];
    /// <summary>Das Gerät beantwortet die USV-MIB.</summary>
    public bool IsUps { get; set; }
    /// <summary>ARP-Tabelle des Geräts (IP → MAC), wenn es routet; liefert MAC-Adressen für Geräte hinter dem Router.</summary>
    public Dictionary<string, string> ArpTable { get; set; } = [];
    /// <summary>Eigene Adressen eines Routers als "ip/präfix" (ipAddrTable); verbinden in der Netzwerkkarte seine Subnetze.</summary>
    public List<string> InterfaceAddresses { get; set; } = [];
}

/// <param name="Level">Füllstand in Einheiten von <paramref name="Capacity"/>; negativ = unbekannt (-3 = vorhanden, Menge unbekannt).</param>
public sealed record SupplyFacts(string Description, int Level, int Capacity);

/// <summary>Was die Prüfung einer Adresse ohne Anmeldung ergeben hat.</summary>
public sealed class DeviceObservation
{
    public required string IpAddress { get; init; }
    public bool PingReply { get; set; }
    public SortedSet<int> OpenPorts { get; set; } = [];
    public string? ReverseName { get; set; }
    public string? MacAddress { get; set; }
    /// <summary>NetBIOS- oder DNS-Name, den ein Windows-Rechner über SMB selbst nennt.</summary>
    public string? WindowsName { get; set; }
    public string? SshBanner { get; set; }
    /// <summary>Erste Zeile eines FTP-, Telnet- oder SMTP-Grußes.</summary>
    public string? Banner { get; set; }
    public string? HttpServer { get; set; }
    public string? HttpTitle { get; set; }
    /// <summary>Realm einer HTTP-Anmeldung (Basic/Digest); nennt bei Geräten oft das Modell.</summary>
    public string? HttpRealm { get; set; }
    /// <summary>Subjekt und Aussteller des TLS-Zertifikats (Port 443 oder ein anderer HTTPS-Port).</summary>
    public string? TlsSubject { get; set; }
    public string? TlsIssuer { get; set; }
    public SnmpFacts? Snmp { get; set; }

    public bool Responded => PingReply || OpenPorts.Count > 0 || Snmp is not null;
}

/// <param name="Reached">Das Ziel hat geantwortet.</param>
public sealed record TraceResult(IReadOnlyList<string?> Hops, bool Reached);

/// <summary>Prüft eine einzelne Adresse ohne Anmeldung: Ping, Standardports, Banner, SNMP.</summary>
public interface IDeviceProbe
{
    /// <param name="communities">SNMP-Communities in der Reihenfolge, in der sie versucht werden; leer = kein SNMP.</param>
    Task<DeviceObservation> ProbeAsync(System.Net.IPAddress address, IReadOnlyList<string> communities, CancellationToken ct = default);

    /// <summary>Traceroute per Ping mit steigender TTL: die antwortenden Router der Reihe nach (null = keine Antwort), ohne das Ziel.</summary>
    Task<TraceResult> TraceAsync(System.Net.IPAddress target, CancellationToken ct = default);

    /// <summary>MAC-Adressen aus der ARP-Tabelle des Servers (nur Geräte im selben Netzsegment); Schlüssel = IP.</summary>
    IReadOnlyDictionary<string, string> LocalNeighbors();

    /// <summary>Die eigenen IPv4-Adressen des Servers mit Präfixlänge: zeigen der Netzwerkkarte, in welchen Netzen er selbst steht.</summary>
    IReadOnlyList<(string Ip, int Prefix)> LocalAddresses() => [];
}
