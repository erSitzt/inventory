using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// vCenter der Simulation: ein Cluster mit drei ESXi-Hosts (2 × 16 Kerne) und Server-VMs, deren UUIDs zu den simulierten
/// Rechnern VM-* passen (wie ein echter Gast in vertauschter Bytefolge), dazu ein einzelner Host außerhalb des Clusters
/// und VMs ohne Eintrag im Inventar.
/// </summary>
public sealed class SimulatedHypervisorSource : IHypervisorSource
{
    public static readonly string[] Guests = ["VM-SQL01", "VM-SQL02", "VM-APP01", "VM-APP02", "VM-APP03", "VM-FILE01", "VM-WEB01", "VM-RDS01"];

    public Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default)
    {
        var hosts = new List<HypervisorHostInfo>
        {
            new("host-11", "esx01.corp.example", "PROD-CLUSTER", 2, 32, "PowerEdge R750", true),
            new("host-12", "esx02.corp.example", "PROD-CLUSTER", 2, 32, "PowerEdge R750", true),
            new("host-13", "esx03.corp.example", "PROD-CLUSTER", 2, 32, "PowerEdge R750", true),
            new("host-21", "esx-dmz01.corp.example", null, 1, 12, "PowerEdge R650", true),
        };
        var vms = Guests.Select((name, i) => new HypervisorVmInfo($"vm-{100 + i}", name, SimulatedInventory.VmwareUuid(name),
                name.StartsWith("VM-SQL", StringComparison.Ordinal) ? 8 : 4, hosts[i % 3].Id, true, name.ToLowerInvariant() + ".corp.example", "Microsoft Windows Server 2022 (64-bit)"))
            .Concat(
            [
                new("vm-200", "VM-DMZ-PROXY", Guid.NewGuid().ToString(), 2, "host-21", true, "dmz-proxy", "Microsoft Windows Server 2019 (64-bit)"),
                new("vm-201", "appliance-backup", Guid.NewGuid().ToString(), 4, "host-11", true, null, "Other 4.x or later Linux (64-bit)"),
                new("vm-202", "VM-OLD-TEST", Guid.NewGuid().ToString(), 2, "host-12", false, null, "Microsoft Windows Server 2016 (64-bit)"),
            ])
            .ToList();
        return Task.FromResult(new HypervisorInventory(hosts, vms, "VMware vCenter Server 8.0.3 (simulated)"));
    }
}
