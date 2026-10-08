using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Liefert eine feste Beispiel-Domäne für den Simulationsmodus.</summary>
public sealed class SimulatedDirectorySource : IDirectorySource
{
    public Task<List<DirectoryComputer>> GetComputersAsync(DirectoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(Enumerable.Range(1, 12)
            .Select(i => new DirectoryComputer($"AD-PC-{i:D3}", $"ad-pc-{i:D3}.corp.example", "Windows 11 Enterprise", i % 4 == 0 ? "Besprechungsraum" : null))
            .ToList());
}
