namespace Inventory.Core.Model;

/// <summary>Ein Subnetz, dessen Adressen verwaltet werden: belegt, frei, veraltet, reserviert.</summary>
public class IpSubnet
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Netz als CIDR auf der Netzadresse (10.0.1.0/24), /20 bis /30.</summary>
    public required string Cidr { get; set; }
    public int? Vlan { get; set; }
    public string? Gateway { get; set; }
    /// <summary>Adressen, die nicht vergeben werden (z. B. der DHCP-Bereich), im Format von <see cref="IpRanges.Expand"/>.</summary>
    public string? Excluded { get; set; }
    public string? Notes { get; set; }
    /// <summary>Wird regelmäßig geprüft (Ping, ARP, DNS).</summary>
    public bool ScanEnabled { get; set; } = true;
    public int ScanIntervalHours { get; set; } = 12;
    /// <summary>Beginn der letzten Prüfung; Antworten dieser Prüfung tragen dieselbe Zeit.</summary>
    public DateTime? LastScanUtc { get; set; }
    public string? LastResult { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Was die Prüfungen über eine Adresse wissen; nur für Adressen, zu denen es überhaupt etwas gibt.</summary>
public class IpObservation
{
    public int Id { get; set; }
    public int SubnetId { get; set; }
    public required string Address { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    /// <summary>Letzte Antwort auf Ping.</summary>
    public DateTime? LastReplyUtc { get; set; }
    /// <summary>Zuletzt in der ARP-Tabelle gefundene MAC-Adresse; bleibt als Spur stehen.</summary>
    public string? MacAddress { get; set; }
    public DateTime? MacSeenUtc { get; set; }
    /// <summary>Name aus dem Reverse-Lookup (PTR) bei der letzten Prüfung.</summary>
    public string? PtrName { get; set; }
    /// <summary>Der PTR-Name löst wieder auf diese Adresse auf; false = vermutlich ein alter Eintrag.</summary>
    public bool? PtrConfirmed { get; set; }
    /// <summary>Zeitstempel des PTR-Eintrags im AD (dynamisch registriert); null = statisch oder unbekannt.</summary>
    public DateTime? PtrRefreshedUtc { get; set; }
    /// <summary>Namen mit einem A-Eintrag auf diese Adresse, durch Komma getrennt.</summary>
    public string? ARecords { get; set; }
    /// <summary>Jüngster Zeitstempel der A-Einträge im AD; null = statisch oder unbekannt.</summary>
    public DateTime? ARefreshedUtc { get; set; }
}

/// <summary>Eine Adresse, die jemand für sich vorgemerkt hat; wird nie als frei angeboten.</summary>
public class IpReservation
{
    public int Id { get; set; }
    public int SubnetId { get; set; }
    public required string Address { get; set; }
    public string? Note { get; set; }
    public string? ReservedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Danach wird die Reservierung von selbst aufgehoben; null = unbefristet.</summary>
    public DateTime? ExpiresUtc { get; set; }
}
