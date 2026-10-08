namespace Inventory.Core.Model;

/// <summary>Wer hat wann was ausgelöst.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public required string User { get; set; }
    public required string Action { get; set; }
    public string? Details { get; set; }
}
