using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Liefert je Rechnername stabile Fantasie-Ergebnisse; für die Entwicklung ohne Zielrechner (z. B. unter WSL).</summary>
public sealed class SimulatedHostProbe : IHostProbe
{
    public Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        var hash = SimulatedInventory.StableHash(host);
        var ip = $"10.0.{hash % 250 + 1}.{hash / 250 % 250 + 1}";
        ProbeResult result = hash % 5 == 0
            ? new ProbeResult(false, ip, "Simuliert: offline")
            : new ProbeResult(true, ip);
        return Task.FromResult(result);
    }
}
