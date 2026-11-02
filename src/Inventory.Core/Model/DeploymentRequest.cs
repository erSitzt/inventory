namespace Inventory.Core.Model;

public enum DeploymentRequestStatus
{
    /// <summary>Wartet darauf, dass ein Administrator in der Oberfläche freigibt oder ablehnt.</summary>
    Pending,
    /// <summary>Freigegeben; das Paket ist freigegeben und das Deployment läuft.</summary>
    Started,
    Rejected,
}

/// <summary>
/// Ein KI-Assistent (MCP) möchte ein noch nicht freigegebenes Paket verteilen. Erst wenn ein angemeldeter Administrator das
/// Paket ansieht und „Freigeben und starten“ wählt, wird es freigegeben und das Deployment beginnt.
/// </summary>
public class DeploymentRequest
{
    public int Id { get; set; }
    public int PackageId { get; set; }
    public Package? Package { get; set; }
    public List<int> ComputerIds { get; set; } = [];
    /// <summary>Wer anfragt, wie im Protokoll (z. B. API token "build" (anna)).</summary>
    public required string RequestedBy { get; set; }
    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Begründung des Assistenten, z. B. "Hotfix für Ticket 4711".</summary>
    public string? Reason { get; set; }
    public bool ScanAfter { get; set; } = true;
    public bool RetryOffline { get; set; }
    public bool RespectWindow { get; set; }
    public DeploymentRequestStatus Status { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedUtc { get; set; }
    /// <summary>Begründung einer Ablehnung.</summary>
    public string? Note { get; set; }
    public int? DeploymentId { get; set; }
}
