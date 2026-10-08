namespace Inventory.Core.Model;

public class Computer
{
    public int Id { get; set; }
    /// <summary>Hostname oder FQDN, unter dem der Rechner im Netz erreichbar ist.</summary>
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? IpAddress { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastProbeUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }
    /// <summary>Fehler des letzten Scan-Versuchs; null, wenn er erfolgreich war.</summary>
    public string? LastScanError { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Überschreibt den globalen Standard-Transport; null = Standard verwenden.</summary>
    public TransportKind? Transport { get; set; }

    /// <summary>Überschreibt die Standard-Zugangsdaten; null = Standard verwenden.</summary>
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }

    // Aus dem letzten Inventar-Scan
    public string? Domain { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? BiosVersion { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsRelease { get; set; }
    public string? OsArchitecture { get; set; }
    public DateTime? OsInstalledUtc { get; set; }
    public DateTime? LastBootUtc { get; set; }
    public string? CpuName { get; set; }
    public int? CpuCores { get; set; }
    public int? CpuLogicalProcessors { get; set; }
    public long? MemoryBytes { get; set; }
    public string? LoggedOnUser { get; set; }

    public List<ComputerSoftware> Software { get; set; } = [];
    public List<ComputerHotfix> Hotfixes { get; set; } = [];
    public List<ComputerDisk> Disks { get; set; } = [];
    public List<ComputerNetworkAdapter> NetworkAdapters { get; set; } = [];
    public List<ComputerWindowsService> Services { get; set; } = [];

    /// <summary>Statische Sammlungen, denen der Rechner fest zugeordnet ist.</summary>
    public List<Collection> Collections { get; set; } = [];
}
