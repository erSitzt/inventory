using Inventory.Core.Abstractions;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <param name="Result">Das Ergebnis unter der Adresse, die gezählt hat.</param>
/// <param name="NameNotFound">Der DNS kennt die Adresse des Rechners (meist seinen Namen) nicht.</param>
/// <param name="ViaKnownIp">Geprüft wurde ersatzweise unter der zuletzt bekannten IP-Adresse.</param>
public sealed record ComputerProbeResult(ProbeResult Result, bool NameNotFound, bool ViaKnownIp);

/// <summary>
/// Wie der Server einen Rechner erreicht, dessen Namen der DNS nicht kennt: über die zuletzt bekannte IP-Adresse, aber nur,
/// wenn sich dort der gemeinte Rechner mit seinem Namen meldet. Eine alte Adresse kann längst einem anderen Rechner gehören.
/// </summary>
public static class ComputerReach
{
    /// <summary>Erreichbarkeit unter der Adresse des Rechners; kennt der DNS den Namen nicht, unter seiner zuletzt bekannten IP.</summary>
    public static async Task<ComputerProbeResult> ProbeComputerAsync(this IHostProbe probe, Computer computer, CancellationToken ct = default)
    {
        var result = await probe.ProbeAsync(computer.Address, ct);
        if (!result.NameNotFound || computer.FallbackAddress is not { } ip)
            return new ComputerProbeResult(result, result.NameNotFound, ViaKnownIp: false);
        var known = await probe.ProbeAsync(ip, ct);
        // Antwortet dort niemand mit Namen oder ein anderer Rechner, gilt der gemeinte als nicht erreichbar.
        var confirmed = known.Online && known.MachineName is { } name && !MachineNames.Differs(computer.Name, name);
        return new ComputerProbeResult(known with { Online = confirmed }, NameNotFound: true, ViaKnownIp: true);
    }

    /// <summary>
    /// Die Adresse für eine Verbindung (Job, Installation): die des Rechners, oder die zuletzt bekannte IP, wenn der DNS den
    /// Namen sicher nicht kennt. Bei der IP muss der Job prüfen, dass er auf dem gemeinten Rechner läuft.
    /// </summary>
    public static async Task<(string Host, bool ViaKnownIp)> ConnectHostAsync(this IHostProbe probe, Computer computer, CancellationToken ct = default) =>
        computer.FallbackAddress is { } ip && !await probe.ResolvesAsync(computer.Address, ct) ? (ip, true) : (computer.Address, false);
}
