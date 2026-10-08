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

    /// <summary>SHA-256 des Schlüssels, mit dem sich der Agent dieses Rechners ausweist; null = kein Agent angemeldet.</summary>
    public string? AgentKeyHash { get; set; }
    public string? AgentVersion { get; set; }
    public DateTime? AgentLastSeenUtc { get; set; }

    // Aus dem letzten Inventar-Scan
    /// <summary>Name, den der Rechner selbst meldet; kann vom Eintrag abweichen (veralteter DNS-Eintrag, CNAME, IP-Adresse).</summary>
    public string? ReportedName { get; set; }

    /// <summary>Name laut Reverse-DNS der zuletzt aufgelösten Adresse; nur ein Hinweis.</summary>
    public string? ReverseDnsName { get; set; }

    /// <summary>true, wenn unter dem eingetragenen Namen ein Rechner mit anderem Namen geantwortet hat.</summary>
    public bool NameMismatch => Differs(ReportedName);

    /// <summary>true, wenn der Rechner sich noch nicht selbst gemeldet hat, Reverse-DNS aber einen anderen Namen nennt.</summary>
    public bool ReverseDnsMismatch => ReportedName is null && Differs(ReverseDnsName);

    /// <summary>NetBIOS-Namen sind auf 15 Zeichen begrenzt; so meldet sich ein Rechner mit längerem DNS-Namen.</summary>
    public const int NetbiosNameLength = 15;

    /// <summary>Der Name, unter dem sich der Rechner melden müsste; null, wenn eine IP-Adresse eingetragen ist.</summary>
    public string? ExpectedMachineName
    {
        get
        {
            if (System.Net.IPAddress.TryParse(Name, out _))
                return null;
            var label = Name.Split('.')[0];
            return label.Length > NetbiosNameLength ? label[..NetbiosNameLength] : label;
        }
    }

    private bool Differs(string? actual)
    {
        if (actual is null || ExpectedMachineName is not { } expected)
            return false;
        var label = actual.Split('.')[0];
        if (label.Length > NetbiosNameLength)
            label = label[..NetbiosNameLength];
        return !string.Equals(expected, label, StringComparison.OrdinalIgnoreCase);
    }
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
    public List<ComputerLocalUser> LocalUsers { get; set; } = [];
    public List<ComputerLocalAdmin> LocalAdmins { get; set; } = [];

    /// <summary>Statische Sammlungen, denen der Rechner fest zugeordnet ist.</summary>
    public List<Collection> Collections { get; set; } = [];
}
