namespace Inventory.Core.Model;

/// <summary>Worauf eine Alarmregel reagiert. Die Zahlen stehen in der Datenbank: nur anhängen, nie umsortieren.</summary>
public enum AlertKind
{
    /// <summary>Ein Rechner kommt neu in eine Sammlung.</summary>
    CollectionEntered = 0,
    /// <summary>Eine Sammlung hat mindestens so viele Mitglieder wie die Schwelle.</summary>
    CollectionCount = 1,
    /// <summary>Ein Deployment ist abgeschlossen und hatte fehlgeschlagene Rechner.</summary>
    DeploymentFailed = 2,
    /// <summary>Ein Zeitplan konnte nicht laufen oder wurde verpasst.</summary>
    ScheduleFailed = 3,
    AdSyncFailed = 4,
    BackupFailed = 5,
    /// <summary>Die Selbstaktualisierung des Servers ist gescheitert.</summary>
    UpdateFailed = 6,
    /// <summary>Ein Rechner wurde neu angelegt (von Hand, per Import, AD-Abgleich oder Agent).</summary>
    NewComputer = 7,
    /// <summary>Mindestens so viele Rechner wie die Schwelle haben einen Fehler beim letzten Scan.</summary>
    ScanErrors = 8,
    /// <summary>Für eine Lizenz sind mehr Installationen gefunden als Lizenzen vorhanden.</summary>
    LicenseExceeded = 9,
    /// <summary>Der Gerätescan hat eine Adresse gefunden, die vorher nicht bekannt war.</summary>
    NewDevice = 10,
}

/// <summary>Eine Regel, die bei einem Ereignis oder Zustand eine Nachricht über die Benachrichtigungskanäle schickt.</summary>
public class AlertRule
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public AlertKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Für die Sammlungs-Regeln; wird die Sammlung gelöscht, ruht die Regel.</summary>
    public int? CollectionId { get; set; }
    public Collection? Collection { get; set; }
    /// <summary>Für <see cref="AlertKind.CollectionCount"/> und <see cref="AlertKind.ScanErrors"/>.</summary>
    public int? Threshold { get; set; }
    /// <summary>So lange nach einer Nachricht schickt die Regel keine weitere; 0 = jede.</summary>
    public int CooldownMinutes { get; set; }
    /// <summary>Leer = alle eingeschalteten Kanäle.</summary>
    public List<NotificationChannel> Channels { get; set; } = [];

    /// <summary>Was die Regel beim letzten Durchlauf gesehen hat (Mitglieder, Zeitpunkt, über der Schwelle): gemeldet wird nur die Veränderung.</summary>
    public string? State { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastFiredUtc { get; set; }
    public string? LastMessage { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public static bool NeedsCollection(AlertKind kind) => kind is AlertKind.CollectionEntered or AlertKind.CollectionCount;
    public static bool NeedsThreshold(AlertKind kind) => kind is AlertKind.CollectionCount or AlertKind.ScanErrors;
    /// <summary>Wird von einem Dienst gemeldet, statt aus dem Datenbestand abgelesen zu werden.</summary>
    public static bool IsEvent(AlertKind kind) => kind is AlertKind.ScheduleFailed or AlertKind.AdSyncFailed or AlertKind.BackupFailed or AlertKind.UpdateFailed;
}
