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
    /// <summary>Gesetzt = Offline-Rechner werden bis zu diesem Zeitpunkt nachgeholt.</summary>
    public DateTime? RetryUntilUtc { get; set; }
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
}
