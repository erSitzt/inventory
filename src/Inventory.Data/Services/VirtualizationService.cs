using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Woher ein Host bekannt ist.</summary>
public enum VirtualHostSource
{
    /// <summary>Inventarisierter Rechner mit Hyper-V.</summary>
    HyperV,
    /// <summary>ESXi-Host aus dem vCenter.</summary>
    VCenter,
    /// <summary>Host aus einem XCP-ng-Pool.</summary>
    XcpNg,
}

/// <param name="GuestComputerId">Der inventarisierte Rechner dieser VM; null = nicht inventarisiert.</param>
public sealed record VirtualMachineView(string Name, int? VirtualCpus, bool? Running, string? GuestOs, int? GuestComputerId, string? GuestName);

/// <param name="ComputerId">Bei Hyper-V der inventarisierte Host.</param>
/// <param name="CpuCores">Physische Kerne insgesamt.</param>
public sealed record VirtualHostView(VirtualHostSource Source, int Id, string Name, string? Cluster, int? CpuSockets, int? CpuCores, string? Model, int? ComputerId,
    IReadOnlyList<VirtualMachineView> VirtualMachines);

/// <summary>Hosts und ihre virtuellen Maschinen aus allen Quellen (Hyper-V-Hosts im Inventar, vCenter) in einer Sicht.</summary>
public sealed class VirtualizationService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    public async Task<List<VirtualHostView>> HostsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var names = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        string? Name(int? id) => id is { } value && names.TryGetValue(value, out var name) ? name : null;
        var result = new List<VirtualHostView>();

        foreach (var host in await db.HypervisorHosts.AsNoTracking().Include(h => h.VirtualMachines).ToListAsync(ct))
            result.Add(new VirtualHostView(host.Kind == Inventory.Core.Abstractions.HypervisorKind.XcpNg ? VirtualHostSource.XcpNg : VirtualHostSource.VCenter, host.Id, host.Name, host.Cluster, host.CpuSockets, host.CpuCores, host.Model, null,
                host.VirtualMachines.OrderBy(v => v.Name)
                    .Select(v => new VirtualMachineView(v.Name, v.VirtualCpus, v.PoweredOn, v.GuestOs, v.GuestComputerId, Name(v.GuestComputerId))).ToList()));

        // Hyper-V: Rechner, die VMs melden oder denen sich Gäste über die Gastdienste zuordnen.
        var guests = await db.Computers.AsNoTracking().Where(c => c.HostComputerId != null).ToListAsync(ct);
        var hostIds = await db.HostedVirtualMachines.Select(v => v.ComputerId).Distinct().ToListAsync(ct);
        hostIds = hostIds.Union(guests.Select(g => g.HostComputerId!.Value)).ToList();
        var hosts = await db.Computers.AsNoTracking().Include(c => c.VirtualMachines).Where(c => hostIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var host in hosts)
        {
            var vms = host.VirtualMachines.OrderBy(v => v.Name)
                .Select(v => new VirtualMachineView(v.Name, v.VirtualCpus, v.Running, null, v.GuestComputerId, Name(v.GuestComputerId))).ToList();
            // Gäste, die der Host nicht selbst meldet (z. B. ohne Rechte auf die Hyper-V-Verwaltung).
            vms.AddRange(guests.Where(g => g.HostComputerId == host.Id && host.VirtualMachines.All(v => v.GuestComputerId != g.Id))
                .Select(g => new VirtualMachineView(g.Name, g.CpuCores, null, g.OsName, g.Id, g.Name)));
            result.Add(new VirtualHostView(VirtualHostSource.HyperV, host.Id, host.Name, host.ClusterName, host.CpuSockets,
                host.CpuSockets is { } sockets && host.CpuCoresPerSocket is { } perSocket ? sockets * perSocket : host.CpuCores, host.Model, host.Id, vms));
        }
        return result.OrderBy(h => h.Cluster is null).ThenBy(h => h.Cluster, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Inventarisierte virtuelle Maschinen ohne bekannten Host: Lücken für die Lizenzberechnung.</summary>
    public async Task<List<Computer>> GuestsWithoutHostAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.Computers.AsNoTracking().Where(c => c.HostComputerId == null && c.HypervisorHostId == null && c.LicensingScanUtc != null).ToListAsync(ct);
        return candidates.Where(c => c.IsVirtual).OrderBy(c => c.Name).ToList();
    }

    public async Task<HypervisorHost?> HypervisorHostAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.HypervisorHosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
    }
}
