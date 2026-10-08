using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Liefert je Rechnername stabile Fantasie-Ergebnisse; für die Entwicklung ohne Zielrechner (z. B. unter WSL).</summary>
public sealed class SimulatedHostProbe : IHostProbe
{
    public Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        var hash = SimulatedInventory.StableHash(host);
        var ip = $"10.0.{hash % 250 + 1}.{hash / 250 % 250 + 1}";
        // Namen, die mit "alias" beginnen, zeigen in der Simulation auf einen anders heißenden Rechner.
        var label = host.Split('.')[0].ToUpperInvariant();
        var actual = label.StartsWith("ALIAS", StringComparison.Ordinal) ? $"PC-{hash % 900 + 100}" : label;
        ProbeResult result = hash % 5 == 0
            ? new ProbeResult(false, ip, "Simuliert: offline")
            : new ProbeResult(true, ip, null, actual, $"{actual.ToLowerInvariant()}.corp.example");
        return Task.FromResult(result);
    }
}
