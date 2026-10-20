namespace Inventory.Core.Model;

/// <summary>Gruppe von Rechnern: statisch (feste Mitglieder) oder dynamisch (Regeln über Inventardaten).</summary>
public class Collection
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsDynamic { get; set; }
    /// <summary>true = alle Regeln müssen zutreffen, false = eine genügt.</summary>
    public bool MatchAll { get; set; } = true;
    public List<CollectionRule> Rules { get; set; } = [];
    /// <summary>Nur für statische Sammlungen.</summary>
    public List<Computer> Members { get; set; } = [];
    /// <summary>Gesetzt = Deployments, die Wartungsfenster beachten, laufen auf den Mitgliedern nur in diesem Zeitraum.</summary>
    public MaintenanceWindow? MaintenanceWindow { get; set; }
}

public enum RuleField
{
    ComputerName,
    /// <summary>Der Rechner meldet einen anderen Namen als eingetragen.</summary>
    NameMismatch = 100,
    /// <summary>Mitglied der lokalen Administratorengruppe (Name enthält …).</summary>
    LocalAdmin = 101,
    /// <summary>Mindestens ein Firewall-Profil ist abgeschaltet.</summary>
    FirewallOff = 102,
    /// <summary>Kein Echtzeit-Virenschutz aktiv.</summary>
    AntivirusProblem = 103,
    RebootRequired = 104,
    PendingUpdates = 105,
    /// <summary>Tage seit der letzten erfolgreichen Update-Installation.</summary>
    DaysSinceUpdate = 106,
    /// <summary>Das Computerkonto fehlt im Active Directory oder ist dort deaktiviert.</summary>
    MissingInDirectory = 107,
    /// <summary>Remotedesktop-Verbindungen sind erlaubt.</summary>
    RdpEnabled = 108,
    /// <summary>Remotedesktop ist erlaubt, ohne die Anmeldung auf Netzwerkebene zu verlangen.</summary>
    RdpWithoutNla = 109,
    UacOff = 110,
    Smb1Enabled = 111,
    CredentialGuard = 112,
    LsaProtection = 113,
    /// <summary>Das vordefinierte Administratorkonto ist aktiv.</summary>
    BuiltinAdminEnabled = 114,
    /// <summary>Alter des Kennworts des vordefinierten Administratorkontos in Tagen.</summary>
    BuiltinAdminPasswordAgeDays = 115,
    /// <summary>Windows ist nicht aktiviert.</summary>
    NotActivated = 116,
    /// <summary>Speicherintegrität (HVCI) läuft.</summary>
    MemoryIntegrity = 117,
    /// <summary>Anzahl ausstehender sicherheitsrelevanter Updates.</summary>
    PendingSecurityUpdates = 118,
    /// <summary>Installierter Hotfix (KB-Nummer); verneint = der Hotfix fehlt.</summary>
    Hotfix = 119,
    /// <summary>Ausstehendes Update (KB-Nummer oder Titel); verneint = steht nicht aus.</summary>
    PendingUpdate = 120,
    /// <summary>Organisationseinheit des Computerkontos: lesbarer Pfad ("Clients/Laptops") oder Distinguished Name.</summary>
    DirectoryOu = 121,
    /// <summary>Gruppe, in der das Computerkonto direkt Mitglied ist.</summary>
    DirectoryGroup = 122,
    /// <summary>Tage seit der letzten Anmeldung des Computerkontos an der Domäne.</summary>
    DaysSinceDirectoryLogon = 123,
    DirectoryDescription = 124,
    Tag = 125,
    Location = 126,
    Owner = 127,
    CostCenter = 128,
    /// <summary>Tage bis zum Ende der Garantie; negativ = abgelaufen.</summary>
    WarrantyDaysLeft = 129,
    PrimaryUser = 130,
    InventoryNumber = 131,
    Domain = 132,
    SerialNumber = 133,
    CpuName = 134,
    OsArchitecture = 135,
    /// <summary>"beginnt mit 10.20." wählt ein Subnetz.</summary>
    IpAddress = 136,
    /// <summary>Freier Platz auf dem Systemlaufwerk in GB.</summary>
    DiskFreeGb = 137,
    DaysSinceBoot = 138,
    /// <summary>Tage seit dem letzten erfolgreichen Scan.</summary>
    ScanAgeDays = 139,
    AgentPresent = 140,
    /// <summary>Windows-Dienst nach Name; <see cref="CollectionRule.Value2"/> grenzt optional auf "Running" oder "Stopped" ein.</summary>
    Service = 141,
    /// <summary>Mitglied einer anderen Sammlung; der Wert ist deren Kennung.</summary>
    InCollection = 142,
    /// <summary>Geräteklasse: Desktop, Laptop, Tablet, Server oder VirtualMachine.</summary>
    DeviceType = 143,
    SecureBoot = 144,
    TpmReady = 145,
    /// <summary>BitLocker-Schutz des Systemlaufwerks ist eingeschaltet.</summary>
    SystemDriveProtected = 146,
    FirmwareType = 147,
    Gpu = 148,
    BatteryHealthPercent = 149,
    /// <summary>Mindestens ein physischer Datenträger meldet einen anderen Zustand als "in Ordnung".</summary>
    DiskHealthProblem = 150,
    /// <summary>App-Paket (MSIX/AppX) vorhanden: Paketname oder Anzeigename.</summary>
    App = 151,
    /// <summary>Weder Anmeldung an der Domäne noch Sichtung durch das Inventory seit der eingestellten Frist.</summary>
    DirectoryInactive = 152,
    Online = 1,
    Scanned,
    OsName,
    OsVersion,
    OsRelease,
    Manufacturer,
    Model,
    LoggedOnUser,
    MemoryGb,
    /// <summary>Installierte Software; <see cref="CollectionRule.Value2"/> grenzt optional auf Versionen unterhalb dieses Werts ein.</summary>
    Software,
}

public enum RuleOperator
{
    Contains,
    NotContains,
    Equals,
    NotEquals,
    StartsWith,
    LessThan,
    GreaterOrEqual,
}

public class CollectionRule
{
    public RuleField Field { get; set; }
    public RuleOperator Operator { get; set; }
    public string Value { get; set; } = "";
    public string? Value2 { get; set; }
}
