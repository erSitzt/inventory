namespace Inventory.Core.Model;

/// <summary>
/// Die Kennzahlen der Übersicht an einem Tag. Nur Summen: was sich an einzelnen Rechnern geändert hat, steht im Verlauf.
/// </summary>
public class DailySnapshot
{
    public int Id { get; set; }
    /// <summary>Der Tag in der Ortszeit des Servers.</summary>
    public DateOnly Date { get; set; }
    public int Computers { get; set; }
    public int Online { get; set; }
    public int Scanned { get; set; }
    public int ScanErrors { get; set; }
    /// <summary>Rechner, deren letzter Scan älter als eine Woche ist.</summary>
    public int StaleScans { get; set; }
    public int FirewallOff { get; set; }
    public int AntivirusProblems { get; set; }
    public int RebootRequired { get; set; }
    public int WithPendingUpdates { get; set; }
    /// <summary>Rechner, deren Systemlaufwerk nachweislich nicht verschlüsselt ist.</summary>
    public int DriveUnprotected { get; set; }
    public int WarrantyEnding { get; set; }
    public int Agents { get; set; }
}
