namespace Inventory.Core.Model;

public class Credential
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>DOMÄNE\benutzer, benutzer@domäne oder .\benutzer für lokale Konten.</summary>
    public required string Username { get; set; }
    /// <summary>Über <see cref="Abstractions.ISecretProtector"/> verschlüsseltes Passwort.</summary>
    public required string ProtectedPassword { get; set; }
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
