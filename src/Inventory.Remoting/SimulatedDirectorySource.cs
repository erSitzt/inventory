using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Liefert eine feste Beispiel-Domäne für den Simulationsmodus; AD-PC-010 und -011 haben sich seit Monaten nicht angemeldet.</summary>
public sealed class SimulatedDirectorySource : IDirectorySource
{
    public Task<List<DirectoryComputer>> GetComputersAsync(DirectoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(Enumerable.Range(1, 12)
            .Select(i => new DirectoryComputer($"AD-PC-{i:D3}", $"ad-pc-{i:D3}.corp.example", "Windows 11 Enterprise", i % 4 == 0 ? "Besprechungsraum" : null, Enabled: i != 12,
                DistinguishedName: $"CN=AD-PC-{i:D3},OU={(i % 3 == 0 ? "Laptops" : "Desktops")},OU=Clients,DC=corp,DC=example",
                Groups: i % 2 == 0 ? ["Pilot-Updates"] : [], LastLogonUtc: DateTime.UtcNow.AddDays(i is 10 or 11 ? -100 - i * 10 : -i * 4)))
            .ToList());
}
