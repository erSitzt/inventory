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
