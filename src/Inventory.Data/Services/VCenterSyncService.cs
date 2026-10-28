using System.Globalization;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="Url">Adresse des vCenters; leer = nicht eingerichtet.</param>
/// <param name="Thumbprint">SHA-256-Fingerabdruck eines selbst signierten Zertifikats, dem vertraut wird.</param>
public sealed record VCenterSettings(bool Enabled, string? Url, int? CredentialId, string? Thumbprint, int IntervalHours, DateTime? LastRunUtc, string? LastResult);

/// <summary>Ergebnis eines Abgleichs mit dem vCenter.</summary>
public sealed record VCenterSyncResult(int Hosts, int Clusters, int VirtualMachines, int Matched, string? Version);

/// <summary>
/// Gleicht Hosts, Cluster und VMs mit dem vCenter ab (nur lesend) und ordnet die VMs den inventarisierten Rechnern zu. Gespeichert
/// wird der jeweils letzte Stand; Hosts und VMs, die das vCenter nicht mehr führt, verschwinden.
/// </summary>
public sealed class VCenterSyncService(IDbContextFactory<InventoryDbContext> dbFactory, IHypervisorSource source, SettingsService settings, CredentialService credentials,
    TimeProvider time, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    private readonly SemaphoreSlim _running = new(1, 1);

    public const int DefaultIntervalHours = 6;

    public async Task<VCenterSettings> GetSettingsAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(SettingKeys.VCenterEnabled, ct) == bool.TrueString,
        await settings.GetAsync(SettingKeys.VCenterUrl, ct),
        int.TryParse(await settings.GetAsync(SettingKeys.VCenterCredentialId, ct), out var credentialId) ? credentialId : null,
        await settings.GetAsync(SettingKeys.VCenterThumbprint, ct),
        int.TryParse(await settings.GetAsync(SettingKeys.VCenterIntervalHours, ct), out var hours) && hours > 0 ? hours : DefaultIntervalHours,
        DateTime.TryParse(await settings.GetAsync(SettingKeys.VCenterLastRunUtc, ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) ? last : null,
        await settings.GetAsync(SettingKeys.VCenterLastResult, ct));

    /// <exception cref="ArgumentException">Die Adresse ist keine https-Adresse.</exception>
    public async Task SaveSettingsAsync(bool enabled, string? url, int? credentialId, string? thumbprint, int intervalHours, CancellationToken ct = default)
    {
        var address = string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        if (address is not null && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(L["The vCenter address must start with https://."]);
        await settings.SetAsync(SettingKeys.VCenterEnabled, enabled.ToString(), ct);
        await settings.SetAsync(SettingKeys.VCenterUrl, address, ct);
        await settings.SetAsync(SettingKeys.VCenterCredentialId, credentialId?.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.VCenterThumbprint, CertificateThumbprints.Normalize(thumbprint), ct);
        await settings.SetAsync(SettingKeys.VCenterIntervalHours, Math.Clamp(intervalHours, 1, 24 * 7).ToString(CultureInfo.InvariantCulture), ct);
    }

    /// <summary>Gleicht ab, wenn eingeschaltet und die Frist seit dem letzten Lauf um ist.</summary>
    /// <returns>Das Ergebnis, wenn ein Abgleich lief; sonst null.</returns>
    public async Task<VCenterSyncResult?> SyncIfDueAsync(CancellationToken ct = default)
    {
        var current = await GetSettingsAsync(ct);
        if (!current.Enabled || string.IsNullOrWhiteSpace(current.Url) || current.LastRunUtc is { } last && time.GetUtcNow().UtcDateTime - last < TimeSpan.FromHours(current.IntervalHours))
            return null;
        try
        {
            return await SyncAsync(ct);
        }
        catch (RemoteException)
        {
            // Steht schon im letzten Ergebnis; der nächste Versuch folgt nach der Frist.
            return null;
        }
    }

    /// <exception cref="RemoteException">Das vCenter ist nicht erreichbar, lehnt die Anmeldung ab oder ist nicht eingerichtet.</exception>
    public async Task<VCenterSyncResult> SyncAsync(CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            var current = await GetSettingsAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;
            HypervisorInventory inventory;
            try
            {
                if (string.IsNullOrWhiteSpace(current.Url))
                    throw new RemoteException(L["Please enter the vCenter address first."]);
                var credential = await credentials.GetAsync(current.CredentialId, ct) ?? throw new RemoteException(L["Please choose credentials for the vCenter."]);
                inventory = await source.ReadAsync(new HypervisorConnection(current.Url, credential, current.Thumbprint), ct);
            }
            catch (RemoteException ex)
            {
                await settings.SetAsync(SettingKeys.VCenterLastRunUtc, now.ToString("O", CultureInfo.InvariantCulture), ct);
                await settings.SetAsync(SettingKeys.VCenterLastResult, L["Failed: {0}", ex.Message], ct);
                throw;
            }
            var result = await StoreAsync(inventory, now, ct);
            await settings.SetAsync(SettingKeys.VCenterLastRunUtc, now.ToString("O", CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(SettingKeys.VCenterLastResult,
                L["{0} hosts in {1} clusters, {2} virtual machines, {3} of them matched to inventoried computers.", result.Hosts, result.Clusters, result.VirtualMachines, result.Matched], ct);
            return result;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Übernimmt den gelesenen Stand und ordnet die VMs zu.</summary>
    internal async Task<VCenterSyncResult> StoreAsync(HypervisorInventory inventory, DateTime now, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hosts = await db.HypervisorHosts.ToDictionaryAsync(h => h.ExternalId, ct);
        foreach (var info in inventory.Hosts)
        {
            if (!hosts.TryGetValue(info.Id, out var host))
                db.HypervisorHosts.Add(host = hosts[info.Id] = new HypervisorHost { ExternalId = info.Id, Name = info.Name });
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

        var vms = await db.HypervisorVms.ToDictionaryAsync(v => v.ExternalId, ct);
        foreach (var info in inventory.VirtualMachines)
        {
            if (!vms.TryGetValue(info.Id, out var vm))
                db.HypervisorVms.Add(vm = vms[info.Id] = new HypervisorVm { ExternalId = info.Id, Name = info.Name });
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
        var matched = await db.HypervisorVms.CountAsync(v => v.GuestComputerId != null, ct);
        return new VCenterSyncResult(inventory.Hosts.Count, inventory.Hosts.Select(h => h.Cluster).Where(c => c is not null).Distinct().Count(),
            inventory.VirtualMachines.Count, matched, inventory.Version);
    }
}
