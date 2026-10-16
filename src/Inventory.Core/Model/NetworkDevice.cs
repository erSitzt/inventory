namespace Inventory.Core.Model;

/// <summary>Art eines Netzwerkgeräts. Werte nur anhängen, nie umsortieren.</summary>
public enum DeviceKind
{
    Unknown = 0,
    Printer = 1,
    Switch = 2,
    Router = 3,
    Firewall = 4,
    AccessPoint = 5,
    Storage = 6,
    /// <summary>Linux-/Unix-Rechner (Server oder Appliance ohne genauere Einordnung).</summary>
    Linux = 7,
    Windows = 8,
    Hypervisor = 9,
    Camera = 10,
    Phone = 11,
    Ups = 12,
    /// <summary>Verwaltungsschnittstelle eines Servers (iLO, iDRAC, IPMI).</summary>
    Management = 13,
    /// <summary>Kleingeräte wie Sensoren, Steckdosen, Mediengeräte.</summary>
    Iot = 14,
    Apple = 15,
}

/// <summary>Ein IP-Bereich, den der Server nach Geräten absucht.</summary>
public class NetworkRange
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>CIDR (10.0.0.0/24), Bereich (10.0.0.10-10.0.0.50 oder 10.0.0.10-50) oder einzelne Adressen; mehrere durch Komma oder Zeilenumbruch.</summary>
    public required string Addresses { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Über <see cref="Abstractions.ISecretProtector"/> verschlüsselte SNMP-Communities (eine je Zeile); null = die allgemeine Einstellung.</summary>
    public string? ProtectedCommunities { get; set; }
    public string? Notes { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public string? LastResult { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Ein Gerät im Netz, gefunden durch den Gerätescan. Die Adresse ist der Schlüssel; wandert ein Gerät mit bekannter MAC auf eine andere Adresse, zieht der Eintrag mit.</summary>
public class NetworkDevice
{
    public int Id { get; set; }
    public required string IpAddress { get; set; }
    public string? MacAddress { get; set; }
    /// <summary>Hersteller laut MAC-Adresse.</summary>
    public string? MacVendor { get; set; }
    /// <summary>Vom Benutzer vergebener Name; sonst gilt <see cref="DiscoveredName"/>.</summary>
    public string? Name { get; set; }
    /// <summary>sysName, Windows-Name oder Reverse-DNS.</summary>
    public string? DiscoveredName { get; set; }
    public string DisplayName => Name ?? DiscoveredName ?? IpAddress;

    public DeviceKind DetectedKind { get; set; }
    /// <summary>Vom Benutzer festgelegte Art; überstimmt die Erkennung.</summary>
    public DeviceKind? KindOverride { get; set; }
    public DeviceKind Kind => KindOverride ?? DetectedKind;
    /// <summary>Woran die Art erkannt wurde, für die Anzeige.</summary>
    public string? KindReason { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    /// <summary>Geschätztes Betriebssystem (z. B. aus dem SSH-Banner oder sysDescr).</summary>
    public string? OsGuess { get; set; }

    /// <summary>Offene TCP-Ports, kommagetrennt und aufsteigend.</summary>
    public string? OpenPorts { get; set; }
    public bool PingReply { get; set; }
    public bool SnmpResponded { get; set; }
    public string? SysDescr { get; set; }
    public string? SysObjectId { get; set; }
    public string? SysLocation { get; set; }
    public string? SysContact { get; set; }
    public long? UptimeSeconds { get; set; }
    public long? PageCount { get; set; }
    public string? SshBanner { get; set; }
    public string? Banner { get; set; }
    public string? HttpServer { get; set; }
    public string? HttpTitle { get; set; }
    public string? TlsSubject { get; set; }
    public string? ReverseName { get; set; }

    /// <summary>Der inventarisierte Rechner unter dieser Adresse, wenn es einer ist.</summary>
    public int? ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public int? NetworkRangeId { get; set; }
    public NetworkRange? NetworkRange { get; set; }
    public string? Notes { get; set; }

    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public List<DeviceSupply> Supplies { get; set; } = [];

    public IEnumerable<int> Ports => (OpenPorts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse);
}

/// <summary>Verbrauchsmaterial eines Druckers (Toner, Trommel, Resttonerbehälter) aus der Drucker-MIB.</summary>
public class DeviceSupply
{
    public int Id { get; set; }
    public int NetworkDeviceId { get; set; }
    public required string Description { get; set; }
    public int Level { get; set; }
    public int Capacity { get; set; }

    /// <summary>Füllstand in Prozent; null, wenn das Gerät keine Menge meldet.</summary>
    public int? Percent => Capacity > 0 && Level >= 0 ? Math.Clamp(Level * 100 / Capacity, 0, 100) : null;
}
