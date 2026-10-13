namespace Inventory.Core.Model;

public class Computer
{
    public int Id { get; set; }
    /// <summary>Hostname oder FQDN, unter dem der Rechner im Netz erreichbar ist.</summary>
    public required string Name { get; set; }
    /// <summary>
    /// Voller DNS-Name (FQDN) aus dem Verzeichnis, vom Agenten oder aus dem Scan; null = nicht bekannt.
    /// Verbindungen laufen über <see cref="Address"/>, auch wenn der Eintrag nur den kurzen Namen trägt.
    /// </summary>
    public string? DnsName { get; set; }
    /// <summary>Unter diesem Namen wird der Rechner im Netz angesprochen (Scan, Erreichbarkeit, Deployment, Fernzugriff).</summary>
    public string Address => MachineNames.Address(Name, DnsName);
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

    /// <summary>
    /// MAC-Adresse für Wake-on-LAN, von Hand eingetragen; null = die Adapter des letzten Scans.
    /// Nötig, wenn der Rechner über eine andere Karte aufwacht als die zuletzt gesehenen (z. B. eine Dockingstation).
    /// </summary>
    public string? WakeMac { get; set; }

    // Von Hand gepflegte Bestandsangaben
    public string? Owner { get; set; }
    public string? Location { get; set; }
    public string? CostCenter { get; set; }
    public string? InventoryNumber { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public DateOnly? WarrantyUntil { get; set; }
    /// <summary>Durch ", " getrennt, siehe <see cref="AssetTags"/>.</summary>
    public string? Tags { get; set; }
    public string? Notes { get; set; }
    public IReadOnlyList<string> TagList => AssetTags.Parse(Tags);
    /// <summary>Tage bis zum Ende der Garantie (negativ = abgelaufen); null, wenn kein Datum eingetragen ist.</summary>
    public int? WarrantyDaysLeft(DateOnly today) => WarrantyUntil is { } until ? until.DayNumber - today.DayNumber : null;
    public AssetData Asset => new(Owner, Location, CostCenter, InventoryNumber, PurchaseDate, WarrantyUntil, Tags, Notes);

    /// <summary>
    /// Wer das Gerät überwiegend nutzt: der Benutzer, der in den letzten 30 Tagen an den meisten Tagen bei einem Scan
    /// interaktiv angemeldet war (Konsole oder Remotedesktop). Konten, mit denen der Server auf die Rechner zugreift,
    /// zählen nie; ohne Beobachtung null.
    /// </summary>
    public string? PrimaryUser { get; set; }

    // Active Directory
    /// <summary>Wann die Synchronisation das Computerkonto zuletzt gefunden hat; null = nie, der Eintrag ist von Hand gepflegt.</summary>
    public DateTime? DirectorySeenUtc { get; set; }
    /// <summary>Seit wann das Konto im Verzeichnis fehlt (oder deaktiviert ist und deaktivierte ausgeschlossen sind); null = vorhanden.</summary>
    public DateTime? DirectoryMissingSinceUtc { get; set; }
    /// <summary>Das Computerkonto ist im Verzeichnis deaktiviert.</summary>
    public bool DirectoryDisabled { get; set; }
    public bool MissingInDirectory => DirectoryMissingSinceUtc is not null;
    /// <summary>Container des Computerkontos als Distinguished Name, z. B. OU=Laptops,OU=Clients,DC=corp,DC=example.</summary>
    public string? DirectoryPath { get; set; }
    /// <summary>Lesbarer Pfad der Organisationseinheit, z. B. "Clients/Laptops".</summary>
    public string? DirectoryOu => DirectoryNames.Path(DirectoryPath);
    /// <summary>Namen der Gruppen, in denen das Konto direkt Mitglied ist, einer je Zeile.</summary>
    public string? DirectoryGroups { get; set; }
    public IEnumerable<string> DirectoryGroupNames => (DirectoryGroups ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    /// <summary>Beschreibung des Computerkontos im Verzeichnis.</summary>
    public string? DirectoryDescription { get; set; }
    /// <summary>Letzte Anmeldung des Kontos an der Domäne; das Verzeichnis gleicht den Wert nur etwa alle zwei Wochen ab.</summary>
    public DateTime? DirectoryLastLogonUtc { get; set; }

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
    /// <summary>Betriebsart laut Defender ("Normal", "Passive Mode", …); null = nicht ermittelt.</summary>
    public string? DefenderMode { get; set; }
    public DateTime? DefenderLastQuickScanUtc { get; set; }
    /// <summary>Beim Sicherheitscenter registrierte und eingeschaltete Virenschutzprogramme, durch Komma getrennt.</summary>
    public string? AntivirusProducts { get; set; }
    /// <summary>Registrierte, aber ausgeschaltete Virenschutzprogramme, durch Komma getrennt.</summary>
    public string? AntivirusProductsInactive { get; set; }
    /// <summary>Ein aktives Virenschutzprogramm meldet veraltete Definitionen (Sicherheitscenter) bzw. die Defender-Signatur ist älter als eine Woche.</summary>
    public bool? AntivirusDefinitionsOutdated { get; set; }
    public DateTime? UpdateLastSearchUtc { get; set; }
    public DateTime? UpdateLastInstallUtc { get; set; }
    public bool? UpdateRebootRequired { get; set; }
    public int? UpdatePendingCount { get; set; }
    /// <summary>Davon sicherheitsrelevant, siehe <see cref="Core.PendingUpdates.IsSecurity"/>.</summary>
    public int? UpdatePendingSecurityCount { get; set; }
    /// <summary>Titel der ausstehenden Updates, einer je Zeile.</summary>
    public string? UpdatePending { get; set; }
    public string? UpdateServer { get; set; }

    // Sicherheitsrelevante Einstellungen des Betriebssystems; null = nicht ermittelt
    public bool? RdpEnabled { get; set; }
    public bool? RdpNla { get; set; }
    public bool? UacEnabled { get; set; }
    /// <summary>0 = nie benachrichtigen … 3 = immer benachrichtigen.</summary>
    public int? UacLevel { get; set; }
    public bool? Smb1Enabled { get; set; }
    public bool? CredentialGuard { get; set; }
    public bool? MemoryIntegrity { get; set; }
    public bool? LsaProtection { get; set; }
    public bool? BuiltinAdminEnabled { get; set; }
    public int? BuiltinAdminPasswordAgeDays { get; set; }
    public bool? WindowsActivated { get; set; }

    /// <summary>true, sobald ein Scan mindestens eine dieser Einstellungen geliefert hat.</summary>
    public bool PostureKnown =>
        RdpEnabled is not null || RdpNla is not null || UacEnabled is not null || UacLevel is not null || Smb1Enabled is not null
        || CredentialGuard is not null || MemoryIntegrity is not null || LsaProtection is not null || BuiltinAdminEnabled is not null
        || BuiltinAdminPasswordAgeDays is not null || WindowsActivated is not null;

    // Hardware und Plattformsicherheit aus dem letzten Scan; null = nicht ermittelt
    /// <summary>Wann zuletzt ein Scan Hardware-Details geliefert hat; null = noch nie (älterer Runner oder nicht gescannt).</summary>
    public DateTime? HardwareScanUtc { get; set; }
    /// <summary>Gehäusetyp laut SMBIOS, siehe <see cref="Chassis"/>.</summary>
    public int? ChassisType { get; set; }
    /// <summary>"UEFI" oder "BIOS".</summary>
    public string? FirmwareType { get; set; }
    public bool? SecureBoot { get; set; }
    public DateTime? BiosDateUtc { get; set; }
    public bool? TpmPresent { get; set; }
    public string? TpmVersion { get; set; }
    public bool? TpmReady { get; set; }
    /// <summary>Namen der Grafikkarten, durch Komma getrennt.</summary>
    public string? Gpu { get; set; }
    public int? MemorySlots { get; set; }
    public bool? BatteryPresent { get; set; }
    public int? BatteryHealthPercent { get; set; }
    public int? BatteryCycleCount { get; set; }
    /// <summary>BitLocker-Schutz des Systemlaufwerks; null = nicht ermittelt.</summary>
    public bool? SystemDriveProtected { get; set; }

    // Fernzugriff, aus dem letzten Scan
    /// <summary>Sitzungsnummer des beim Scan angemeldeten Benutzers, für die RDP-Spiegelung.</summary>
    public int? SessionId { get; set; }
    public string? TeamViewerId { get; set; }
    public string? AnyDeskId { get; set; }

    /// <summary>true, wenn mindestens ein Firewall-Profil abgeschaltet ist.</summary>
    public bool FirewallOff => FirewallDomain == false || FirewallPrivate == false || FirewallPublic == false;

    /// <summary>true, wenn kein Echtzeitschutz läuft: weder Defender noch ein anderes registriertes und eingeschaltetes Virenschutzprogramm.</summary>
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
    public List<ComputerPendingUpdate> PendingUpdates { get; set; } = [];
    public List<ComputerUserProfile> UserProfiles { get; set; } = [];
    public List<ComputerMemoryModule> MemoryModules { get; set; } = [];
    public List<ComputerPhysicalDisk> PhysicalDisks { get; set; } = [];
    public List<ComputerVolumeEncryption> Volumes { get; set; } = [];
    public List<ComputerMonitor> Monitors { get; set; } = [];
    public List<ComputerPrinter> Printers { get; set; } = [];
    public List<ComputerApp> Apps { get; set; } = [];

    /// <summary>Statische Sammlungen, denen der Rechner fest zugeordnet ist.</summary>
    public List<Collection> Collections { get; set; } = [];
}
