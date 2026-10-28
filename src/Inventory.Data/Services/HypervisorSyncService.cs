using System.Globalization;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Ergebnis eines Abgleichs mit einer Virtualisierungsverwaltung.</summary>
public sealed record HypervisorSyncResult(int Hosts, int Clusters, int VirtualMachines, int Matched, string? Version);

/// <summary>
/// Gleicht Hosts, Cluster bzw. Pools und VMs mit den eingerichteten Virtualisierungsverwaltungen ab (vCenter, XCP-ng, Xen
/// Orchestra; nur lesend) und ordnet die VMs den inventarisierten Rechnern zu. Gespeichert wird je Verwaltung der letzte Stand;
/// was sie nicht mehr führt, verschwindet.
/// </summary>
public sealed class HypervisorSyncService(IDbContextFactory<InventoryDbContext> dbFactory, IEnumerable<IHypervisorSource> sources, SettingsService settings,
    CredentialService credentials, TimeProvider time, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    // Ein Abgleich zur Zeit: die Zuordnung der Gäste betrifft alle Verwaltungen.
    private readonly SemaphoreSlim _running = new(1, 1);

    public const int DefaultIntervalHours = 6;

    /// <summary>Die Arten, für die es eine Quelle gibt.</summary>
    public IReadOnlyList<HypervisorKind> Kinds => sources.Select(s => s.Kind).Distinct().Order().ToList();

    /// <summary>Anzeigename der Art, z. B. "vCenter".</summary>
    public static string Name(HypervisorKind kind) => kind switch
    {
        HypervisorKind.XcpNg => "XCP-ng",
        HypervisorKind.XenOrchestra => "Xen Orchestra",
        _ => "vCenter",
    };

    /// <summary>Hersteller bzw. Plattform für die Gruppierung in Übersichten.</summary>
    public static string Platform(HypervisorKind kind) => kind == HypervisorKind.VCenter ? "VMware" : "XCP-ng";

    public async Task<List<HypervisorManager>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.HypervisorManagers.AsNoTracking().OrderBy(m => m.Kind).ThenBy(m => m.Name).ToListAsync(ct);
    }

    /// <exception cref="ArgumentException">Name leer oder schon vergeben, Adresse keine https-Adresse.</exception>
    public async Task<HypervisorManager> SaveAsync(HypervisorManager manager, CancellationToken ct = default)
    {
        var name = manager.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException(L["Please enter a name."]);
        var address = (manager.Url ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(L["The address must start with https://."]);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.HypervisorManagers.AnyAsync(m => m.Name == name && m.Id != manager.Id, ct))
            throw new ArgumentException(L["The name \"{0}\" is already used.", name]);
        var entity = manager.Id == 0 ? new HypervisorManager { Name = name, Url = address, Kind = manager.Kind }
            : await db.HypervisorManagers.FirstOrDefaultAsync(m => m.Id == manager.Id, ct) ?? throw new KeyNotFoundException(L["Entry {0} not found.", manager.Id]);
        if (manager.Id == 0)
            db.HypervisorManagers.Add(entity);
        entity.Name = name;
        entity.Url = address;
        entity.CredentialId = manager.CredentialId;
        entity.Thumbprint = CertificateThumbprints.Normalize(manager.Thumbprint);
        entity.Enabled = manager.Enabled;
        entity.IntervalHours = Math.Clamp(manager.IntervalHours, 1, 24 * 7);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    /// <summary>Entfernt die Verwaltung mit ihren Hosts und VMs; ihre Gäste verlieren die Zuordnung.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var hostIds = await db.HypervisorHosts.Where(h => h.ManagerId == id).Select(h => h.Id).ToListAsync(ct);
            await db.Computers.Where(c => c.HypervisorHostId != null && hostIds.Contains(c.HypervisorHostId.Value))
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.HypervisorHostId, (int?)null).SetProperty(x => x.HostSource, (string?)null), ct);
            await db.HypervisorManagers.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
            await VirtualHosts.ResolveVCenterAsync(db, ct);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Übernimmt die Einrichtung aus der Zeit mit einer Verwaltung je Art (Einstellungen VCenter.*, XcpNg.*, XenOrchestra.*) als
    /// Verwaltungen; danach sind die alten Einstellungen leer.
    /// </summary>
    public async Task MigrateSettingsAsync(CancellationToken ct = default)
    {
        foreach (var (kind, prefix) in new[] { (HypervisorKind.VCenter, "VCenter."), (HypervisorKind.XcpNg, "XcpNg."), (HypervisorKind.XenOrchestra, "XenOrchestra.") })
        {
            if (await settings.GetAsync(prefix + "Url", ct) is not { Length: > 0 } url)
                continue;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var name = Name(kind).ToLowerInvariant().Replace(" ", "");
                if (!await db.HypervisorManagers.AnyAsync(m => m.Name == name, ct))
                {
                    db.HypervisorManagers.Add(new HypervisorManager
                    {
                        Kind = kind, Name = name, Url = url,
                        CredentialId = int.TryParse(await settings.GetAsync(prefix + "CredentialId", ct), out var credential) && await db.Credentials.AnyAsync(c => c.Id == credential, ct) ? credential : null,
                        Thumbprint = await settings.GetAsync(prefix + "Thumbprint", ct),
                        Enabled = await settings.GetAsync(prefix + "Enabled", ct) == bool.TrueString,
                        IntervalHours = int.TryParse(await settings.GetAsync(prefix + "IntervalHours", ct), out var hours) && hours > 0 ? hours : DefaultIntervalHours,
                    });
                    await db.SaveChangesAsync(ct);
                }
            }
            foreach (var key in new[] { "Url", "CredentialId", "Thumbprint", "Enabled", "IntervalHours", "LastRunUtc", "LastResult" })
                await settings.SetAsync(prefix + key, null, ct);
        }
    }

    /// <summary>Gleicht alle eingeschalteten Verwaltungen ab, deren Frist seit dem letzten Lauf um ist.</summary>
    /// <returns>Name und Ergebnis jedes Abgleichs, der gelungen ist.</returns>
    public async Task<List<(HypervisorManager Manager, HypervisorSyncResult Result)>> SyncDueAsync(CancellationToken ct = default)
    {
        var results = new List<(HypervisorManager, HypervisorSyncResult)>();
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var manager in (await ListAsync(ct)).Where(m => m.Enabled && (m.LastRunUtc is not { } last || now - last >= TimeSpan.FromHours(m.IntervalHours))))
        {
            try
            {
                results.Add((manager, await SyncAsync(manager.Id, ct)));
            }
            catch (RemoteException)
            {
                // Steht im letzten Ergebnis der Verwaltung; der nächste Versuch folgt nach der Frist.
            }
        }
        return results;
    }

    /// <exception cref="RemoteException">Die Verwaltung ist nicht erreichbar, lehnt die Anmeldung ab oder ist nicht vollständig eingerichtet.</exception>
    public async Task<HypervisorSyncResult> SyncAsync(int managerId, CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            HypervisorManager manager;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                manager = await db.HypervisorManagers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == managerId, ct) ?? throw new RemoteException(L["Entry {0} not found.", managerId]);
            var now = time.GetUtcNow().UtcDateTime;
            HypervisorInventory inventory;
            try
            {
                var credential = await credentials.GetAsync(manager.CredentialId, ct) ?? throw new RemoteException(L["Please choose credentials."]);
                var source = sources.FirstOrDefault(s => s.Kind == manager.Kind) ?? throw new RemoteException($"No source for {Name(manager.Kind)}.");
                inventory = await source.ReadAsync(new HypervisorConnection(manager.Url, credential, manager.Thumbprint), ct);
            }
            catch (RemoteException ex)
            {
                await RecordAsync(managerId, now, L["Failed: {0}", ex.Message], ct);
                throw;
            }
            var result = await StoreAsync(manager, inventory, now, ct);
            await RecordAsync(managerId, now,
                L["{0} hosts in {1} clusters, {2} virtual machines, {3} of them matched to inventoried computers.", result.Hosts, result.Clusters, result.VirtualMachines, result.Matched], ct);
            return result;
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task RecordAsync(int managerId, DateTime now, string result, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.HypervisorManagers.Where(m => m.Id == managerId)
            .ExecuteUpdateAsync(m => m.SetProperty(x => x.LastRunUtc, now).SetProperty(x => x.LastResult, result), ct);
    }

    /// <summary>Übernimmt den gelesenen Stand und ordnet die VMs zu.</summary>
    internal async Task<HypervisorSyncResult> StoreAsync(HypervisorManager manager, HypervisorInventory inventory, DateTime now, CancellationToken ct)
    {
        var (kind, managerId) = (manager.Kind, manager.Id);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Nur den Stand dieser Verwaltung ersetzen; die anderen bleiben, wie sie sind.
        var hosts = await db.HypervisorHosts.Where(h => h.ManagerId == managerId).ToDictionaryAsync(h => h.ExternalId, ct);
        foreach (var info in inventory.Hosts)
        {
            if (!hosts.TryGetValue(info.Id, out var host))
                db.HypervisorHosts.Add(host = hosts[info.Id] = new HypervisorHost { ManagerId = managerId, Kind = kind, ExternalId = info.Id, Name = info.Name });
            host.Name = info.Name;
            host.Cluster = info.Cluster;
            host.CpuSockets = info.CpuSockets;
            host.CpuCores = info.CpuCores;
            host.Model = info.Model;
            host.Connected = info.Connected;
            host.SeenUtc = now;
        }
        var seenHosts = inventory.Hosts.Select(h => h.Id).ToHashSet();
        db.HypervisorHosts.RemoveRange(hosts.Values.Where(h => !seenHosts.Contains(h.ExternalId)));
        await db.SaveChangesAsync(ct);

        var vms = await db.HypervisorVms.Where(v => v.ManagerId == managerId).ToDictionaryAsync(v => v.ExternalId, ct);
        foreach (var info in inventory.VirtualMachines)
        {
            if (!vms.TryGetValue(info.Id, out var vm))
                db.HypervisorVms.Add(vm = vms[info.Id] = new HypervisorVm { ManagerId = managerId, Kind = kind, ExternalId = info.Id, Name = info.Name });
            vm.Name = info.Name;
            vm.BiosUuid = BiosUuids.Normalize(info.BiosUuid);
            vm.VirtualCpus = info.VirtualCpus;
            vm.HostId = info.HostId is { } hostId && hosts.TryGetValue(hostId, out var host) && seenHosts.Contains(hostId) ? host.Id : null;
            vm.PoweredOn = info.PoweredOn;
            vm.GuestHostName = info.GuestHostName;
            vm.GuestOs = info.GuestOs;
            vm.SeenUtc = now;
        }
        var seenVms = inventory.VirtualMachines.Select(v => v.Id).ToHashSet();
        db.HypervisorVms.RemoveRange(vms.Values.Where(v => !seenVms.Contains(v.ExternalId)));
        await db.SaveChangesAsync(ct);

        await VirtualHosts.ResolveVCenterAsync(db, ct);
        await transaction.CommitAsync(ct);
        var matched = await db.HypervisorVms.CountAsync(v => v.ManagerId == managerId && v.GuestComputerId != null, ct);
        return new HypervisorSyncResult(inventory.Hosts.Count, inventory.Hosts.Select(h => h.Cluster).Where(c => c is not null).Distinct().Count(),
            inventory.VirtualMachines.Count, matched, inventory.Version);
    }
}
