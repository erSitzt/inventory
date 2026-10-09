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

    // Active Directory
    /// <summary>Wann die Synchronisation das Computerkonto zuletzt gefunden hat; null = nie, der Eintrag ist von Hand gepflegt.</summary>
    public DateTime? DirectorySeenUtc { get; set; }
    /// <summary>Seit wann das Konto im Verzeichnis fehlt (oder deaktiviert ist und deaktivierte ausgeschlossen sind); null = vorhanden.</summary>
    public DateTime? DirectoryMissingSinceUtc { get; set; }
    /// <summary>Das Computerkonto ist im Verzeichnis deaktiviert.</summary>
    public bool DirectoryDisabled { get; set; }
    public bool MissingInDirectory => DirectoryMissingSinceUtc is not null;

    // Aus dem letzten Inventar-Scan
    /// <summary>Name, den der Rechner selbst meldet; kann vom Eintrag abweichen (veralteter DNS-Eintrag, CNAME, IP-Adresse).</summary>
    public string? ReportedName { get; set; }

    /// <summary>Name laut Reverse-DNS der zuletzt aufgelösten Adresse; nur ein Hinweis.</summary>
    public string? ReverseDnsName { get; set; }

    /// <summary>true, wenn unter dem eingetragenen Namen ein Rechner mit anderem Namen geantwortet hat.</summary>
    public bool NameMismatch => Differs(ReportedName);

    /// <summary>true, wenn der Rechner sich noch nicht selbst gemeldet hat, Reverse-DNS aber einen anderen Namen nennt.</summary>
    public bool ReverseDnsMismatch => ReportedName is null && Differs(ReverseDnsName);

    /// <summary>Der Name, unter dem sich der Rechner melden müsste; null, wenn eine IP-Adresse eingetragen ist.</summary>
    public string? ExpectedMachineName => MachineNames.Expected(Name);

    private bool Differs(string? actual) => MachineNames.Differs(Name, actual);

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

    // Sicherheitsstatus aus dem letzten Scan; null = nicht ermittelt
    public bool? FirewallDomain { get; set; }
    public bool? FirewallPrivate { get; set; }
    public bool? FirewallPublic { get; set; }
    public bool? DefenderEnabled { get; set; }
    public bool? DefenderRealTime { get; set; }
    public bool? DefenderTamperProtected { get; set; }
    public string? DefenderSignatureVersion { get; set; }
    public DateTime? DefenderSignatureUpdatedUtc { get; set; }
    /// <summary>Beim Sicherheitscenter registrierte Virenschutzprogramme, durch Komma getrennt.</summary>
    public string? AntivirusProducts { get; set; }
    public DateTime? UpdateLastSearchUtc { get; set; }
    public DateTime? UpdateLastInstallUtc { get; set; }
    public bool? UpdateRebootRequired { get; set; }
    public int? UpdatePendingCount { get; set; }
    /// <summary>Titel der ausstehenden Updates, einer je Zeile.</summary>
    public string? UpdatePending { get; set; }
    public string? UpdateServer { get; set; }

    // Fernzugriff, aus dem letzten Scan
    /// <summary>Sitzungsnummer des beim Scan angemeldeten Benutzers, für die RDP-Spiegelung.</summary>
    public int? SessionId { get; set; }
    public string? TeamViewerId { get; set; }
    public string? AnyDeskId { get; set; }

    /// <summary>true, wenn mindestens ein Firewall-Profil abgeschaltet ist.</summary>
    public bool FirewallOff => FirewallDomain == false || FirewallPrivate == false || FirewallPublic == false;

    /// <summary>true, wenn kein Echtzeitschutz läuft: weder Defender noch ein anderes registriertes Virenschutzprogramm.</summary>
    public bool AntivirusProblem =>
        LastScanUtc is not null && DefenderEnabled is not null && DefenderRealTime != true && !HasThirdPartyAntivirus;

    public bool HasThirdPartyAntivirus =>
        (AntivirusProducts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(p => !p.Contains("Defender", StringComparison.OrdinalIgnoreCase));

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
