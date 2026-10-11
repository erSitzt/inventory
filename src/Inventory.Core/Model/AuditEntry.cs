namespace Inventory.Core.Model;

/// <summary>Wer hat wann was ausgelöst.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public required string User { get; set; }
    public required string Action { get; set; }
    public string? Details { get; set; }
    /// <summary>Rolle der Anmeldung zum Zeitpunkt der Aktion; null bei Einträgen ohne Anmeldung und bei älteren Einträgen.</summary>
    public AccessRole? Role { get; set; }
    /// <summary>Weg, über den die Aktion kam.</summary>
    public AuditSource Source { get; set; }
}

/// <summary>Herkunft eines Protokolleintrags. Neue Werte nur anhängen.</summary>
public enum AuditSource
{
    /// <summary>Nicht erfasst (Einträge aus der Zeit vor dieser Angabe).</summary>
    Unknown = 0,
    Web = 1,
    /// <summary>Verwaltungs-API mit einer Benutzeranmeldung.</summary>
    Api = 2,
    ApiToken = 3,
    Agent = 4,
    /// <summary>Hintergrundläufe des Servers (Zeitplaner, AD-Synchronisation, Updates).</summary>
    System = 5,
}

/// <summary>Wer einen Protokolleintrag auslöst. Ein bloßer Name genügt, wo Rolle und Herkunft nicht bekannt sind.</summary>
public sealed record AuditActor(string User, AccessRole? Role = null, AuditSource Source = AuditSource.Unknown)
{
    public static implicit operator AuditActor(string user) => new(user);

    public static AuditActor System(string label) => new(label, null, AuditSource.System);
}
