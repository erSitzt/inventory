using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>
/// Ordnet virtuelle Maschinen ihrem Host zu. Vorrang hat die BIOS-UUID, die der Host für die VM führt und die der Gast selbst meldet;
/// sonst der Host, den die Hyper-V-Gastdienste nennen, sofern ein Rechner dieses Namens inventarisiert ist.
/// </summary>
public static class VirtualHosts
{
    /// <summary>Nach dem Scan eines Rechners: ihn als Gast zuordnen und, falls er Host ist, seine VMs.</summary>
    public static async Task ResolveAsync(InventoryDbContext db, int computerId, CancellationToken ct = default)
    {
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, ct);
        if (computer is null)
            return;

        // Als Host: jede gemeldete VM mit dem inventarisierten Gast verbinden.
        var machines = await db.HostedVirtualMachines.Where(v => v.ComputerId == computerId).ToListAsync(ct);
        var linked = new HashSet<int>();
        foreach (var machine in machines)
        {
            var guest = await FindByUuidAsync(db, machine.BiosUuid, computerId, ct);
            machine.GuestComputerId = guest?.Id;
            if (guest is null)
                continue;
            guest.HostComputerId = computerId;
            guest.HostSource = HostSources.BiosUuid;
            linked.Add(guest.Id);
        }
        // Gäste, die der Host nicht mehr führt (verschoben oder gelöscht), neu zuordnen.
        var former = await db.Computers.Where(c => c.HostComputerId == computerId && c.HostSource == HostSources.BiosUuid).ToListAsync(ct);
        foreach (var guest in former.Where(g => !linked.Contains(g.Id)))
            await ResolveGuestAsync(db, guest, ct);

        await ResolveGuestAsync(db, computer, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Den Host eines Rechners bestimmen; null, wenn er keine VM ist oder kein Host bekannt ist. Reihenfolge: Hyper-V-Host
    /// mit derselben UUID, vCenter-VM mit derselben UUID, Hostname aus den Hyper-V-Gastdiensten, vCenter-VM gleichen Namens.
    /// </summary>
    internal static async Task ResolveGuestAsync(InventoryDbContext db, Computer guest, CancellationToken ct)
    {
        var candidates = guest.BiosUuid is { } uuid ? new[] { uuid, BiosUuids.Swapped(uuid) } : [];
        if (candidates.Length > 0)
        {
            var machine = await db.HostedVirtualMachines.Where(v => v.BiosUuid != null && candidates.Contains(v.BiosUuid) && v.ComputerId != guest.Id)
                .OrderByDescending(v => v.Running).FirstOrDefaultAsync(ct);
            if (machine is not null)
            {
                machine.GuestComputerId = guest.Id;
                Assign(guest, machine.ComputerId, null, HostSources.BiosUuid);
                return;
            }
            var vm = await db.HypervisorVms.Where(v => v.BiosUuid != null && candidates.Contains(v.BiosUuid)).OrderByDescending(v => v.PoweredOn).FirstOrDefaultAsync(ct);
            if (vm is not null)
            {
                vm.GuestComputerId = guest.Id;
                Assign(guest, null, vm.HostId, HostSources.VCenter);
                return;
            }
        }
        if (guest.ReportedHostName is { Length: > 0 } reported && await FindByNameAsync(db, reported, guest.Id, ct) is { } host)
        {
            Assign(guest, host.Id, null, HostSources.GuestReport);
            return;
        }
        // Ohne UUID-Treffer: eine vCenter-VM, deren Gastname oder Name zum Rechner passt, und die keinem anderen Rechner gehört.
        if (guest.HyperVGuest != true && guest.IsVirtual)
        {
            var named = (await db.HypervisorVms.Where(v => v.GuestComputerId == null || v.GuestComputerId == guest.Id).ToListAsync(ct))
                .FirstOrDefault(v => SameMachineName(v.GuestHostName, guest) || SameMachineName(v.Name, guest));
            if (named is not null)
            {
                named.GuestComputerId = guest.Id;
                Assign(guest, null, named.HostId, HostSources.VCenterName);
                return;
            }
        }
        Assign(guest, null, null, null);
    }

    private static bool SameMachineName(string? name, Computer computer)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var shortName = name.Trim().Split('.')[0];
        return string.Equals(name.Trim(), computer.DnsName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(shortName, computer.Name.Split('.')[0], StringComparison.OrdinalIgnoreCase)
               || string.Equals(shortName, computer.ReportedName, StringComparison.OrdinalIgnoreCase);
    }

    private static void Assign(Computer guest, int? hostComputerId, int? hypervisorHostId, string? source)
    {
        guest.HostComputerId = hostComputerId;
        guest.HypervisorHostId = hypervisorHostId;
        guest.HostSource = source;
    }

    /// <summary>
    /// Nach einem Abgleich mit dem vCenter: jede VM mit ihrem inventarisierten Gast verbinden und Rechner neu zuordnen, die bisher
    /// einer vCenter-VM zugeordnet waren. Rechner mit Hyper-V-Zuordnung über die UUID behalten diese.
    /// </summary>
    public static async Task ResolveVCenterAsync(InventoryDbContext db, CancellationToken ct = default)
    {
        // Die Abfragen unten laufen in der Datenbank: Zwischenstände deshalb jeweils speichern.
        await db.HypervisorVms.ExecuteUpdateAsync(v => v.SetProperty(x => x.GuestComputerId, (int?)null), ct);
        var computers = await db.Computers.Where(c => c.LicensingScanUtc != null || c.HypervisorHostId != null || c.HostSource != null).ToListAsync(ct);
        // Physische Rechner haben keinen Host; nur VMs und bisher zugeordnete Rechner prüfen.
        computers = computers.Where(c => c.IsVirtual || c.HypervisorHostId is not null || c.HostSource is not null).ToList();
        // Erst die Rechner mit UUID, damit ein Namenstreffer keinem Rechner die VM wegnimmt, die ihm über die UUID gehört.
        foreach (var computer in computers.OrderBy(c => c.BiosUuid is null))
        {
            if (computer.HostSource == HostSources.BiosUuid)
                continue;
            await ResolveGuestAsync(db, computer, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<Computer?> FindByUuidAsync(InventoryDbContext db, string? uuid, int hostId, CancellationToken ct)
    {
        if (BiosUuids.Normalize(uuid) is not { } normalized)
            return null;
        var candidates = new[] { normalized, BiosUuids.Swapped(normalized) };
        return await db.Computers.Where(c => c.Id != hostId && c.BiosUuid != null && candidates.Contains(c.BiosUuid)).FirstOrDefaultAsync(ct);
    }

    /// <summary>Über den DNS-Namen, sonst den Namen, sonst den ersten Teil eines vollqualifizierten Namens.</summary>
    private static async Task<Computer?> FindByNameAsync(InventoryDbContext db, string name, int guestId, CancellationToken ct)
    {
        var shortName = name.Split('.')[0];
        var lower = name.ToLowerInvariant();
        // Name vergleicht ohne Groß-/Kleinschreibung (Sortierfolge NOCASE), der DNS-Name nicht.
        var matches = await db.Computers.Where(c => c.Id != guestId && (c.DnsName!.ToLower() == lower || c.Name == name || c.Name == shortName)).ToListAsync(ct);
        return matches.FirstOrDefault(c => string.Equals(c.DnsName, name, StringComparison.OrdinalIgnoreCase))
               ?? matches.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? matches.FirstOrDefault();
    }
}
