namespace Inventory.Core.Abstractions;

/// <param name="MachineName">Name, den der antwortende Rechner selbst nennt (ohne Anmeldung ermittelt); null, wenn unbekannt.</param>
/// <param name="ReverseName">Name laut Reverse-DNS der Adresse; nur ein Hinweis, da auch dieser Eintrag veraltet sein kann.</param>
/// <param name="NameNotFound">Der DNS kennt den Namen nicht (im Unterschied zu einem Rechner, der nicht antwortet).</param>
public sealed record ProbeResult(bool Online, string? IpAddress, string? Error = null, string? MachineName = null, string? ReverseName = null,
    bool NameNotFound = false);

/// <summary>Prüft, ob ein Rechner im Netz erreichbar ist.</summary>
public interface IHostProbe
{
    Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default);

    /// <summary>false nur, wenn der DNS den Namen sicher nicht kennt; bei anderen Fehlern (Zeitüberschreitung) true.</summary>
    Task<bool> ResolvesAsync(string host, CancellationToken ct = default) => Task.FromResult(true);
}
