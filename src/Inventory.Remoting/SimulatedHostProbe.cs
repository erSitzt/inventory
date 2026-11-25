using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Liefert je Rechnername stabile Fantasie-Ergebnisse; für die Entwicklung ohne Zielrechner (z. B. unter WSL).</summary>
public sealed class SimulatedHostProbe : IHostProbe
{
    /// <summary>Welcher simulierte Rechner unter einer Adresse antwortet; gefüllt, sobald ein Name einmal geprüft wurde.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Machines = new();

    /// <summary>Der simulierte Rechner unter dieser IP-Adresse; null, wenn dort keiner bekannt ist oder es keine Adresse ist.</summary>
    internal static string? MachineAt(string host) => Machines.TryGetValue(host, out var machine) ? machine.ToLowerInvariant() : null;

    /// <summary>Namen, die mit "nodns" beginnen, kennt der simulierte DNS nicht.</summary>
    private static bool Unknown(string host) => !System.Net.IPAddress.TryParse(host, out _) && host.StartsWith("nodns", StringComparison.OrdinalIgnoreCase);

    public Task<bool> ResolvesAsync(string host, CancellationToken ct = default) => Task.FromResult(!Unknown(host));

    public Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default)
    {
        if (System.Net.IPAddress.TryParse(host, out _))
            return Task.FromResult(Machines.TryGetValue(host, out var machine)
                ? new ProbeResult(true, host, null, machine, $"{machine.ToLowerInvariant()}.corp.example")
                : new ProbeResult(false, host, "Simuliert: offline"));
        var hash = SimulatedInventory.StableHash(host);
        var ip = $"10.0.{hash % 250 + 1}.{hash / 250 % 250 + 1}";
        // Namen, die mit "alias" beginnen, zeigen in der Simulation auf einen anders heißenden Rechner.
        var label = host.Split('.')[0].ToUpperInvariant();
        var actual = label.StartsWith("ALIAS", StringComparison.Ordinal) ? $"PC-{hash % 900 + 100}" : label;
        Machines[ip] = actual;
        if (Unknown(host))
            return Task.FromResult(new ProbeResult(false, null, "Simuliert: Name nicht im DNS", NameNotFound: true));
        ProbeResult result = hash % 5 == 0
            ? new ProbeResult(false, ip, "Simuliert: offline")
            : new ProbeResult(true, ip, null, actual, $"{actual.ToLowerInvariant()}.corp.example");
        return Task.FromResult(result);
    }
}
