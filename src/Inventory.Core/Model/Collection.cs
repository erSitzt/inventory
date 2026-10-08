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
