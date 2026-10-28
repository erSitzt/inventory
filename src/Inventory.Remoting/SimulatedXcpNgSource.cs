using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// XCP-ng-Pool der Simulation: zwei Hosts (2 × 12 Kerne) im Pool XCP-POOL mit Server-VMs, deren UUIDs zu den simulierten Rechnern
/// XEN-* passen (bei Xen meldet der Gast die UUID der VM unverändert).
/// </summary>
/// <param name="kind">XcpNg oder XenOrchestra: derselbe Pool, einmal direkt, einmal über Xen Orchestra gelesen.</param>
public sealed class SimulatedXcpNgSource(HypervisorKind kind = HypervisorKind.XcpNg) : IHypervisorSource
{
    public static readonly string[] Guests = ["XEN-APP01", "XEN-APP02", "XEN-SQL01", "XEN-WEB01"];

    public HypervisorKind Kind => kind;

    public Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default)
    {
        var hosts = new List<HypervisorHostInfo>
        {
            new("6f1d2c3e-0001-4a5b-8c9d-000000000001", "xcp01.corp.example", "XCP-POOL", 2, 24, "ProLiant DL360 Gen10", true),
            new("6f1d2c3e-0001-4a5b-8c9d-000000000002", "xcp02.corp.example", "XCP-POOL", 2, 24, "ProLiant DL360 Gen10", true),
        };
        var vms = Guests.Select((name, i) => new HypervisorVmInfo(SimulatedInventory.XenUuid(name), name, SimulatedInventory.XenUuid(name),
                name.Contains("SQL", StringComparison.Ordinal) ? 8 : 4, hosts[i % 2].Id, true, name.ToLowerInvariant(), "Microsoft Windows Server 2022 Standard"))
            .Append(new HypervisorVmInfo("7a7a7a7a-0000-4000-8000-000000000099", "xo-appliance", "7a7a7a7a-0000-4000-8000-000000000099", 2, hosts[0].Id, true, "xoa", "Debian GNU/Linux 12"))
            .ToList();
        return Task.FromResult(new HypervisorInventory(hosts, vms, "XCP-ng 8.3.0 (simulated)"));
    }
}
