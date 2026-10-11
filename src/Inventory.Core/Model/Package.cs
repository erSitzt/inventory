using Inventory.Contracts;

namespace Inventory.Core.Model;

/// <summary>Wiederverwendbare Abfolge von Schritten samt zugehöriger Dateien im Repository.</summary>
public class Package
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public List<PackageStep> Steps { get; set; } = [];

    /// <summary>Gesetzt = aus der Paketbibliothek importiert (ID des Katalogeintrags).</summary>
    public string? CatalogId { get; set; }
    /// <summary>Neue Versionen aus der Bibliothek automatisch übernehmen.</summary>
    public bool CatalogAutoUpdate { get; set; }
    /// <summary>Dynamische Sammlung "… veraltet", deren Versionsgrenze mit dem Paket mitwandert.</summary>
    public int? CatalogCollectionId { get; set; }

    /// <summary>Woran der Runner erkennt, dass das Paket schon installiert ist; null = keine Erkennung, immer ausführen.</summary>
    public StepConditions? Detection { get; set; }
    /// <summary>Nach dem letzten Schritt muss <see cref="Detection"/> zutreffen, sonst gilt das Ziel als fehlgeschlagen.</summary>
    public bool VerifyAfterInstall { get; set; }

    /// <summary>
    /// Wer das Paket zum Verteilen freigegeben hat; null = noch in Arbeit. Operatoren und Zeitpläne verteilen nur freigegebene
    /// Pakete. Jede Änderung an Schritten, Erkennung oder Dateien hebt die Freigabe auf.
    /// </summary>
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedUtc { get; set; }
    public bool IsApproved => ApprovedUtc is not null;
}

public class PackageStep
{
    public int Id { get; set; }
    public int PackageId { get; set; }
    public int Order { get; set; }
    /// <summary>Gesetzt = dieser Schritt führt ein anderes Paket aus; <see cref="Definition"/> liefert dann nur den Namen.</summary>
    public int? NestedPackageId { get; set; }
    public Package? NestedPackage { get; set; }
    /// <summary>Was der Runner ausführt; Dateipfade sind relativ zum Repository-Ordner des Pakets.</summary>
    public JobStep Definition { get; set; } = new();
}

public enum TargetStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    /// <summary>Rechner war nicht erreichbar; wird nachgeholt, sobald er online ist.</summary>
    WaitingOffline,
    /// <summary>Ein Neustart-Schritt läuft; die restlichen Schritte folgen, sobald der Rechner wieder da ist.</summary>
    WaitingReboot = 6,
    /// <summary>Die Erkennung des Pakets traf schon vor dem ersten Schritt zu; es wurde nichts ausgeführt.</summary>
    AlreadyInstalled = 7,
    /// <summary>Das Wartungsfenster des Rechners ist geschlossen; das Ziel startet, sobald es beginnt.</summary>
    WaitingWindow = 8,
}

public static class TargetStatusExtensions
{
    /// <summary>Das Paket ist auf dem Rechner vorhanden: gerade installiert oder schon vorher da.</summary>
    public static bool IsSuccess(this TargetStatus status) => status is TargetStatus.Succeeded or TargetStatus.AlreadyInstalled;

    /// <summary>Noch nicht entschieden: das Ziel wartet auf den Rechner, einen Neustart oder sein Wartungsfenster.</summary>
    public static bool IsWaiting(this TargetStatus status) => status is TargetStatus.WaitingOffline or TargetStatus.WaitingReboot or TargetStatus.WaitingWindow;
}

public static class DeploymentLimits
{
    /// <summary>Vorher sagt ein Prozentwert nichts aus: ein einzelner Fehler unter drei Rechnern wären schon 33 %.</summary>
    public const int MinFinishedForPercent = 5;

    /// <summary>true, wenn bei diesem Stand keine weiteren Rechner mehr gestartet werden sollen.</summary>
    public static bool Reached(int? maxFailures, int? maxFailurePercent, int failed, int finished) =>
        (maxFailures is > 0 && failed >= maxFailures)
        || (maxFailurePercent is > 0 && finished >= MinFinishedForPercent && failed >= 2 && failed * 100 >= maxFailurePercent * finished);
}

/// <summary>Ausführung eines Pakets auf einer Menge von Rechnern.</summary>
public class Deployment
{
    public int Id { get; set; }
    public int? PackageId { get; set; }
    /// <summary>Name und Version zum Zeitpunkt der Ausführung; bleiben erhalten, wenn das Paket geändert oder gelöscht wird.</summary>
    public required string PackageName { get; set; }
    public string? PackageVersion { get; set; }
    public bool ScanAfter { get; set; }
    /// <summary>Bis zu diesem Zeitpunkt werden wartende Ziele (Rechner offline, Wartungsfenster geschlossen) nachgeholt.</summary>
    public DateTime? RetryUntilUtc { get; set; }
    /// <summary>Nicht erreichbare Rechner warten, statt sofort fehlzuschlagen.</summary>
    public bool RetryOffline { get; set; }
    /// <summary>Rechner außerhalb ihres Wartungsfensters warten, bis es beginnt.</summary>
    public bool RespectMaintenanceWindow { get; set; }
    /// <summary>Nach so vielen fehlgeschlagenen Rechnern werden keine weiteren mehr gestartet; null = keine Grenze.</summary>
    public int? MaxFailures { get; set; }
    /// <summary>
    /// Anteil fehlgeschlagener an den abgeschlossenen Rechnern (in Prozent), ab dem keine weiteren mehr gestartet werden.
    /// Greift erst nach <see cref="DeploymentLimits.MinFinishedForPercent"/> abgeschlossenen und zwei fehlgeschlagenen Rechnern.
    /// </summary>
    public int? MaxFailurePercent { get; set; }
    /// <summary>So viele Rechner dieses Deployments laufen höchstens gleichzeitig; null = nur die allgemeine Grenze.</summary>
    public int? MaxParallel { get; set; }
    /// <summary>Gesetzt, sobald eine Fehlergrenze erreicht war: weshalb die restlichen Rechner nicht mehr gestartet wurden.</summary>
    public string? StopReason { get; set; }
    /// <summary>Der Zeitplan, der dieses Deployment gestartet hat; null = von Hand oder der Zeitplan ist gelöscht.</summary>
    public int? ScheduleId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public List<DeploymentTarget> Targets { get; set; } = [];
}

public class DeploymentTarget
{
    public int Id { get; set; }
    public int DeploymentId { get; set; }
    public int? ComputerId { get; set; }
    public required string ComputerName { get; set; }
    public TargetStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public List<StepResult> Steps { get; set; } = [];
    /// <summary>Index des ersten Schritts, der nach dem Neustart noch aussteht (Status <see cref="TargetStatus.WaitingReboot"/>).</summary>
    public int? ResumeFromStep { get; set; }
    /// <summary>Wann der Neustart-Schritt gemeldet wurde; danach beginnt die Wartefrist.</summary>
    public DateTime? RebootUtc { get; set; }
    /// <summary>Betriebssystem-Startzeit des Rechners vor dem Neustart (seine eigene Uhr).</summary>
    public DateTime? BootTimeUtc { get; set; }
}
