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
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Überschreibt den globalen Standard-Transport; null = Standard verwenden.</summary>
    public TransportKind? Transport { get; set; }

    /// <summary>Überschreibt die Standard-Zugangsdaten; null = Standard verwenden.</summary>
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }
}
