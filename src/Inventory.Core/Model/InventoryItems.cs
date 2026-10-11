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
    public int? PasswordAgeDays { get; set; }
    public DateTime? LastLogonUtc { get; set; }
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

/// <summary>Update, das laut letzter Suche des Rechners noch nicht installiert ist.</summary>
public class ComputerPendingUpdate
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Title { get; set; }
    /// <summary>KB-Nummern mit Präfix, durch Komma getrennt; null, wenn das Update keine nennt.</summary>
    public string? KbIds { get; set; }
    /// <summary>Englische Bezeichnung, z. B. "Security Updates"; null, wenn unbekannt.</summary>
    public string? Classification { get; set; }
    /// <summary>"Critical", "Important", "Moderate", "Low" oder null.</summary>
    public string? Severity { get; set; }
    public bool IsDownloaded { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Unter diesem Schlüssel wird dasselbe Update über Rechner hinweg zusammengefasst: die KB-Nummern, sonst der Titel.</summary>
    public string Key => string.IsNullOrEmpty(KbIds) ? Title : KbIds;
    public bool IsSecurity => PendingUpdates.IsSecurity(Classification, Severity, Title);
}

public class ComputerMemoryModule
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Slot { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public string? SerialNumber { get; set; }
    public long CapacityBytes { get; set; }
    public int? SpeedMhz { get; set; }
    public string? Type { get; set; }
}

public class ComputerPhysicalDisk
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Model { get; set; }
    public string? SerialNumber { get; set; }
    /// <summary>"SSD", "HDD" oder null.</summary>
    public string? MediaType { get; set; }
    public string? BusType { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>"Healthy", "Warning" oder "Unhealthy".</summary>
    public string? Health { get; set; }
}

/// <summary>BitLocker-Zustand eines Laufwerks.</summary>
public class ComputerVolumeEncryption
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Drive { get; set; }
    public bool? Protected { get; set; }
    public int? EncryptedPercent { get; set; }
    public string? Method { get; set; }
}

public class ComputerMonitor
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public string? Manufacturer { get; set; }
    public required string Model { get; set; }
    public string? SerialNumber { get; set; }
    public int? Year { get; set; }
}

public class ComputerPrinter
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? Driver { get; set; }
    public string? Port { get; set; }
    public bool Network { get; set; }
    public bool Shared { get; set; }
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
