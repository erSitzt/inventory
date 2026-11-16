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
    /// <summary>Host aus einem XCP-ng-Pool, gelesen über Xen Orchestra.</summary>
    XenOrchestra,
}

/// <param name="GuestComputerId">Der inventarisierte Rechner dieser VM; null = nicht inventarisiert.</param>
public sealed record VirtualMachineView(string Name, int? VirtualCpus, bool? Running, string? GuestOs, int? GuestComputerId, string? GuestName);

/// <param name="ComputerId">Bei Hyper-V der inventarisierte Host.</param>
/// <param name="CpuCores">Physische Kerne insgesamt.</param>
/// <param name="ManagerId">Die Verwaltung (vCenter, Pool-Master, Xen Orchestra), aus der der Host stammt; null bei Hyper-V.</param>
/// <param name="ClusterDrs">vSphere DRS eingeschaltet (null = unbekannt).</param>
/// <param name="ClusterHa">HA des Clusters bzw. Pools eingeschaltet (null = unbekannt).</param>
public sealed record VirtualHostView(VirtualHostSource Source, int Id, string Name, string? Cluster, int? CpuSockets, int? CpuCores, string? Model, int? ComputerId,
    IReadOnlyList<VirtualMachineView> VirtualMachines, int? ManagerId = null, string? ManagerName = null, bool? ClusterDrs = null, bool? ClusterHa = null);

/// <summary>Der Host, auf dem ein inventarisierter Rechner als VM läuft.</summary>
/// <param name="Platform">VMware, XCP-ng oder Hyper-V.</param>
/// <param name="ManagedBy">Die Verwaltung, z. B. "vCenter vcenter1"; null bei Hyper-V.</param>
/// <param name="ComputerId">Bei Hyper-V der inventarisierte Host.</param>
/// <param name="MatchedBy">Woran die VM erkannt wurde: "BIOS UUID", "name" oder "guest report".</param>
/// <param name="SeenUtc">Stand des letzten Abgleichs mit der Verwaltung; mit DRS kann die VM seitdem gewandert sein.</param>
public sealed record GuestHostView(string Name, string Platform, string? Cluster, string? ManagedBy, int? ComputerId, string MatchedBy, bool? ClusterDrs = null,
    bool? ClusterHa = null, DateTime? SeenUtc = null);

/// <summary>Hosts und ihre virtuellen Maschinen aus allen Quellen (Hyper-V-Hosts im Inventar, vCenter) in einer Sicht.</summary>
public sealed class VirtualizationService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    public async Task<List<VirtualHostView>> HostsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var names = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        string? Name(int? id) => id is { } value && names.TryGetValue(value, out var name) ? name : null;
        var result = new List<VirtualHostView>();

        var managers = await db.HypervisorManagers.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        foreach (var host in await db.HypervisorHosts.AsNoTracking().Include(h => h.VirtualMachines).ToListAsync(ct))
            result.Add(new VirtualHostView(host.Kind switch
            {
                Inventory.Core.Abstractions.HypervisorKind.XcpNg => VirtualHostSource.XcpNg,
                Inventory.Core.Abstractions.HypervisorKind.XenOrchestra => VirtualHostSource.XenOrchestra,
                _ => VirtualHostSource.VCenter,
            }, host.Id, host.Name, host.Cluster, host.CpuSockets, host.CpuCores, host.Model, null,
                host.VirtualMachines.OrderBy(v => v.Name)
                    .Select(v => new VirtualMachineView(v.Name, v.VirtualCpus, v.PoweredOn, v.GuestOs, v.GuestComputerId, Name(v.GuestComputerId))).ToList(),
                host.ManagerId, managers.GetValueOrDefault(host.ManagerId), host.ClusterDrs, host.ClusterHa));

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

    /// <summary>
    /// Wie auf der Rechnerseite: zuerst der Host aus einer Virtualisierungsverwaltung, sonst der inventarisierte Hyper-V-Host;
    /// null = keine VM oder Host unbekannt.
    /// </summary>
    public async Task<GuestHostView?> HostOfAsync(Computer computer, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (computer.HypervisorHostId is { } id && await db.HypervisorHosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct) is { } host)
        {
            var manager = await db.HypervisorManagers.Where(m => m.Id == host.ManagerId).Select(m => m.Name).FirstOrDefaultAsync(ct);
            return new GuestHostView(host.Name, HypervisorSyncService.Platform(host.Kind), host.Cluster, $"{HypervisorSyncService.Name(host.Kind)} {manager}".Trim(), null,
                computer.HostSource == HostSources.HypervisorName ? "name" : "BIOS UUID", host.ClusterDrs, host.ClusterHa, DateTime.SpecifyKind(host.SeenUtc, DateTimeKind.Utc));
        }
        if (computer.HostComputerId is { } hostId
            && await db.Computers.AsNoTracking().Where(c => c.Id == hostId).Select(c => new { c.Name, c.ClusterName }).FirstOrDefaultAsync(ct) is { } hyperV)
            return new GuestHostView(hyperV.Name, "Hyper-V", hyperV.ClusterName, null, hostId, computer.HostSource == HostSources.BiosUuid ? "BIOS UUID" : "guest report");
        return null;
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

    /// <summary>Name der Verwaltung, z. B. vcenter1.</summary>
    public async Task<string?> ManagerNameAsync(int managerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.HypervisorManagers.Where(m => m.Id == managerId).Select(m => m.Name).FirstOrDefaultAsync(ct);
    }
}
