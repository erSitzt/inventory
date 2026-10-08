namespace Inventory.Core.Abstractions;

public sealed record ProbeResult(bool Online, string? IpAddress, string? Error = null);

/// <summary>Prüft, ob ein Rechner im Netz erreichbar ist.</summary>
public interface IHostProbe
{
    Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default);
}
