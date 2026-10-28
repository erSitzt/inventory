using System.Globalization;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="Url">Adresse des vCenters bzw. Pool-Masters; leer = nicht eingerichtet.</param>
/// <param name="Thumbprint">SHA-256-Fingerabdruck eines selbst signierten Zertifikats, dem vertraut wird.</param>
public sealed record HypervisorSettings(bool Enabled, string? Url, int? CredentialId, string? Thumbprint, int IntervalHours, DateTime? LastRunUtc, string? LastResult);

/// <summary>Ergebnis eines Abgleichs mit einer Virtualisierungsverwaltung.</summary>
public sealed record HypervisorSyncResult(int Hosts, int Clusters, int VirtualMachines, int Matched, string? Version);

/// <summary>
/// Gleicht Hosts, Cluster bzw. Pools und VMs mit den Virtualisierungsverwaltungen ab (vCenter, XCP-ng; nur lesend) und ordnet die
/// VMs den inventarisierten Rechnern zu. Gespeichert wird je Verwaltung der letzte Stand; was sie nicht mehr führt, verschwindet.
/// </summary>
public sealed class HypervisorSyncService(IDbContextFactory<InventoryDbContext> dbFactory, IEnumerable<IHypervisorSource> sources, SettingsService settings,
    CredentialService credentials, TimeProvider time, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    // Ein Abgleich zur Zeit: die Zuordnung der Gäste betrifft alle Verwaltungen.
    private readonly SemaphoreSlim _running = new(1, 1);

    public const int DefaultIntervalHours = 6;

    /// <summary>Die Verwaltungen, für die es eine Quelle gibt.</summary>
    public IReadOnlyList<HypervisorKind> Kinds => sources.Select(s => s.Kind).Distinct().Order().ToList();

    /// <summary>Anzeigename, z. B. "vCenter".</summary>
    public static string Name(HypervisorKind kind) => kind switch
    {
        HypervisorKind.XcpNg => "XCP-ng",
        _ => "vCenter",
    };

    /// <summary>Präfix der Einstellungen; "VCenter" bleibt aus der Zeit nur mit vCenter.</summary>
    private static string Key(HypervisorKind kind, string name) => (kind == HypervisorKind.XcpNg ? "XcpNg." : "VCenter.") + name;

    public async Task<HypervisorSettings> GetSettingsAsync(HypervisorKind kind, CancellationToken ct = default) => new(
        await settings.GetAsync(Key(kind, "Enabled"), ct) == bool.TrueString,
        await settings.GetAsync(Key(kind, "Url"), ct),
        int.TryParse(await settings.GetAsync(Key(kind, "CredentialId"), ct), out var credentialId) ? credentialId : null,
        await settings.GetAsync(Key(kind, "Thumbprint"), ct),
        int.TryParse(await settings.GetAsync(Key(kind, "IntervalHours"), ct), out var hours) && hours > 0 ? hours : DefaultIntervalHours,
        DateTime.TryParse(await settings.GetAsync(Key(kind, "LastRunUtc"), ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) ? last : null,
        await settings.GetAsync(Key(kind, "LastResult"), ct));

    /// <exception cref="ArgumentException">Die Adresse ist keine https-Adresse.</exception>
    public async Task SaveSettingsAsync(HypervisorKind kind, bool enabled, string? url, int? credentialId, string? thumbprint, int intervalHours, CancellationToken ct = default)
    {
        var address = string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        if (address is not null && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(L["The address must start with https://."]);
        await settings.SetAsync(Key(kind, "Enabled"), enabled.ToString(), ct);
        await settings.SetAsync(Key(kind, "Url"), address, ct);
        await settings.SetAsync(Key(kind, "CredentialId"), credentialId?.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(Key(kind, "Thumbprint"), CertificateThumbprints.Normalize(thumbprint), ct);
        await settings.SetAsync(Key(kind, "IntervalHours"), Math.Clamp(intervalHours, 1, 24 * 7).ToString(CultureInfo.InvariantCulture), ct);
    }

    /// <summary>Gleicht ab, wenn eingeschaltet und die Frist seit dem letzten Lauf um ist.</summary>
    /// <returns>Das Ergebnis, wenn ein Abgleich lief; sonst null.</returns>
    public async Task<HypervisorSyncResult?> SyncIfDueAsync(HypervisorKind kind, CancellationToken ct = default)
    {
        var current = await GetSettingsAsync(kind, ct);
        if (!current.Enabled || string.IsNullOrWhiteSpace(current.Url) || current.LastRunUtc is { } last && time.GetUtcNow().UtcDateTime - last < TimeSpan.FromHours(current.IntervalHours))
            return null;
        try
        {
            return await SyncAsync(kind, ct);
        }
        catch (RemoteException)
        {
            // Steht schon im letzten Ergebnis; der nächste Versuch folgt nach der Frist.
            return null;
        }
    }

    /// <exception cref="RemoteException">Die Verwaltung ist nicht erreichbar, lehnt die Anmeldung ab oder ist nicht eingerichtet.</exception>
    public async Task<HypervisorSyncResult> SyncAsync(HypervisorKind kind, CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            var current = await GetSettingsAsync(kind, ct);
            var now = time.GetUtcNow().UtcDateTime;
            HypervisorInventory inventory;
            try
            {
                if (string.IsNullOrWhiteSpace(current.Url))
                    throw new RemoteException(L["Please enter the address first."]);
                var credential = await credentials.GetAsync(current.CredentialId, ct) ?? throw new RemoteException(L["Please choose credentials."]);
                var source = sources.FirstOrDefault(s => s.Kind == kind) ?? throw new RemoteException($"No source for {Name(kind)}.");
                inventory = await source.ReadAsync(new HypervisorConnection(current.Url, credential, current.Thumbprint), ct);
            }
            catch (RemoteException ex)
            {
                await settings.SetAsync(Key(kind, "LastRunUtc"), now.ToString("O", CultureInfo.InvariantCulture), ct);
                await settings.SetAsync(Key(kind, "LastResult"), L["Failed: {0}", ex.Message], ct);
                throw;
            }
            var result = await StoreAsync(kind, inventory, now, ct);
            await settings.SetAsync(Key(kind, "LastRunUtc"), now.ToString("O", CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(Key(kind, "LastResult"),
                L["{0} hosts in {1} clusters, {2} virtual machines, {3} of them matched to inventoried computers.", result.Hosts, result.Clusters, result.VirtualMachines, result.Matched], ct);
            return result;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Übernimmt den gelesenen Stand und ordnet die VMs zu.</summary>
    internal async Task<HypervisorSyncResult> StoreAsync(HypervisorKind kind, HypervisorInventory inventory, DateTime now, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Nur den Stand dieser Verwaltung ersetzen; die anderen bleiben, wie sie sind.
        var hosts = await db.HypervisorHosts.Where(h => h.Kind == kind).ToDictionaryAsync(h => h.ExternalId, ct);
        foreach (var info in inventory.Hosts)
        {
            if (!hosts.TryGetValue(info.Id, out var host))
                db.HypervisorHosts.Add(host = hosts[info.Id] = new HypervisorHost { Kind = kind, ExternalId = info.Id, Name = info.Name });
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

        var vms = await db.HypervisorVms.Where(v => v.Kind == kind).ToDictionaryAsync(v => v.ExternalId, ct);
        foreach (var info in inventory.VirtualMachines)
        {
            if (!vms.TryGetValue(info.Id, out var vm))
                db.HypervisorVms.Add(vm = vms[info.Id] = new HypervisorVm { Kind = kind, ExternalId = info.Id, Name = info.Name });
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
        var matched = await db.HypervisorVms.CountAsync(v => v.Kind == kind && v.GuestComputerId != null, ct);
        return new HypervisorSyncResult(inventory.Hosts.Count, inventory.Hosts.Select(h => h.Cluster).Where(c => c is not null).Distinct().Count(),
            inventory.VirtualMachines.Count, matched, inventory.Version);
    }
}
