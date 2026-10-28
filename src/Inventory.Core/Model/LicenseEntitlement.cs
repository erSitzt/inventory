namespace Inventory.Core.Model;

/// <summary>Gekaufte Microsoft-Lizenzen (Bestand), z. B. aus einem Volumenlizenzvertrag.</summary>
public class LicenseEntitlement
{
    public int Id { get; set; }
    /// <summary>Produktschlüssel aus der Regeldatei, z. B. WindowsServerDatacenterCore.</summary>
    public required string Product { get; set; }
    /// <summary>Kerne bei Kernlizenzen, sonst Anzahl Lizenzen.</summary>
    public int Quantity { get; set; }
    /// <summary>Produktversion, z. B. 2022.</summary>
    public string? Version { get; set; }
    /// <summary>Software Assurance bzw. Abonnement läuft bis einschließlich; null = keine.</summary>
    public DateOnly? SoftwareAssuranceUntil { get; set; }
    public string? ContractNumber { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public bool SoftwareAssuranceActive(DateOnly today) => SoftwareAssuranceUntil is { } until && until >= today;
}
