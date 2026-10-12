namespace Inventory.Core.Model;

public enum NotificationKind
{
    Email = 0,
    Webhook = 1,
}

/// <summary>Aufbau der Nachricht an einen Webhook.</summary>
public enum WebhookFormat
{
    /// <summary>Schlichtes JSON mit Titel, Text, Dringlichkeit und Link.</summary>
    Generic = 0,
    /// <summary>Adaptive Card für Microsoft Teams (Workflows-Webhook).</summary>
    Teams = 1,
    /// <summary>Blöcke für Slack und dazu kompatible Dienste.</summary>
    Slack = 2,
}

public enum SmtpSecurity
{
    /// <summary>Unverschlüsselt verbinden, dann STARTTLS (üblich auf Port 587).</summary>
    StartTls = 0,
    /// <summary>TLS von Beginn an (üblich auf Port 465).</summary>
    Ssl = 1,
    /// <summary>Unverschlüsselt, etwa für ein Relay im eigenen Netz (Port 25).</summary>
    None = 2,
}

public enum NotificationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Eine Nachricht, wie sie an alle Kanäle geht; jeder Kanal bringt sie in seine Form.</summary>
public sealed record Notification(string Title, string Text, NotificationSeverity Severity = NotificationSeverity.Info, string? Link = null);

/// <summary>Ein Weg, auf dem der Server Nachrichten verschickt: ein Postfach-Verteiler oder ein Webhook.</summary>
public class NotificationChannel
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public NotificationKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; }
    /// <summary>Anmeldung am Mailserver; null = ohne Anmeldung (Relay).</summary>
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }
    public string? Sender { get; set; }
    /// <summary>Empfängeradressen, durch Komma, Semikolon oder Zeilenumbruch getrennt.</summary>
    public string? Recipients { get; set; }

    /// <summary>Über <see cref="Abstractions.ISecretProtector"/> verschlüsselte Adresse: sie enthält bei den meisten Diensten das Zugriffsgeheimnis.</summary>
    public string? ProtectedUrl { get; set; }
    public WebhookFormat WebhookFormat { get; set; }

    public DateTime? LastSentUtc { get; set; }
    /// <summary>Fehler des letzten Versands; null nach einem erfolgreichen.</summary>
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public static IReadOnlyList<string> ParseRecipients(string? recipients) =>
        (recipients ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
