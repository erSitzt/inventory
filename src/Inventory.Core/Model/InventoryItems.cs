namespace Inventory.Core.Model;

public class ComputerSoftware
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallDate { get; set; }
    public bool Is64Bit { get; set; }
    public bool PerUser { get; set; }
    public string? UninstallString { get; set; }
    /// <summary>Bei <see cref="PerUser"/>: SID des Benutzers, in dessen Profil das Programm eingetragen ist.</summary>
    public string? UserSid { get; set; }
    /// <summary>Aus einem früheren Scan übernommen: der Benutzer war beim letzten Scan nicht angemeldet, sein Profil daher nicht lesbar.</summary>
    public bool CarriedOver { get; set; }
}

public class ComputerHotfix
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string HotfixId { get; set; }
    public string? Description { get; set; }
    public string? InstalledOn { get; set; }
}

public class ComputerDisk
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Drive { get; set; }
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long SizeBytes { get; set; }
    public long FreeBytes { get; set; }
}

public class ComputerNetworkAdapter
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? MacAddress { get; set; }
    /// <summary>Mehrere Werte durch Komma getrennt.</summary>
    public string? IpAddresses { get; set; }
    public string? Gateways { get; set; }
    public string? DnsServers { get; set; }
    public bool DhcpEnabled { get; set; }
}

public class ComputerWindowsService
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? State { get; set; }
    public string? StartMode { get; set; }
    public string? Account { get; set; }
}

public class ComputerLocalUser
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? FullName { get; set; }
    public string? Description { get; set; }
    public bool Disabled { get; set; }
    public string? Sid { get; set; }
}

/// <summary>Mitglied der lokalen Administratorengruppe.</summary>
public class ComputerLocalAdmin
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public bool IsGroup { get; set; }
    public string? Sid { get; set; }
}

public enum ChangeKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>Unterschied zwischen zwei aufeinanderfolgenden Scans eines Rechners.</summary>
public class InventoryChange
{
    public long Id { get; set; }
    public int ComputerId { get; set; }
    public DateTime TimeUtc { get; set; }
    /// <summary>Bereich, z. B. "Software" oder "Lokale Administratoren".</summary>
    public required string Category { get; set; }
    public ChangeKind Kind { get; set; }
    public required string Item { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
