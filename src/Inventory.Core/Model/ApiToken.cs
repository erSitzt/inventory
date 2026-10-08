namespace Inventory.Core.Model;

/// <summary>Zugang für Skripte: wird im Kopf "Authorization: Bearer …" mitgeschickt.</summary>
public class ApiToken
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>SHA-256 des Tokens; der Klartext wird nur einmal beim Anlegen angezeigt.</summary>
    public required string TokenHash { get; set; }
    /// <summary>Anfang des Tokens, um es in der Liste wiederzuerkennen.</summary>
    public required string Prefix { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
}
