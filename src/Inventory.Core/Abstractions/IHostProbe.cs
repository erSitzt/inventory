namespace Inventory.Core.Abstractions;

/// <param name="MachineName">Name, den der antwortende Rechner selbst nennt (ohne Anmeldung ermittelt); null, wenn unbekannt.</param>
/// <param name="ReverseName">Name laut Reverse-DNS der Adresse; nur ein Hinweis, da auch dieser Eintrag veraltet sein kann.</param>
public sealed record ProbeResult(bool Online, string? IpAddress, string? Error = null, string? MachineName = null, string? ReverseName = null);

/// <summary>Prüft, ob ein Rechner im Netz erreichbar ist.</summary>
public interface IHostProbe
{
    Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default);
}
