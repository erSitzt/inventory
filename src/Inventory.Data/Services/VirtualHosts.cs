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

    /// <summary>Den Host eines Rechners bestimmen; null, wenn er keine VM ist oder kein Host bekannt ist.</summary>
    private static async Task ResolveGuestAsync(InventoryDbContext db, Computer guest, CancellationToken ct)
    {
        if (guest.BiosUuid is { } uuid)
        {
            var candidates = new[] { uuid, BiosUuids.Swapped(uuid) };
            var machine = await db.HostedVirtualMachines.Where(v => v.BiosUuid != null && candidates.Contains(v.BiosUuid) && v.ComputerId != guest.Id)
                .OrderByDescending(v => v.Running).FirstOrDefaultAsync(ct);
            if (machine is not null)
            {
                machine.GuestComputerId = guest.Id;
                Assign(guest, machine.ComputerId, HostSources.BiosUuid);
                return;
            }
        }
        if (guest.ReportedHostName is { Length: > 0 } reported && await FindByNameAsync(db, reported, guest.Id, ct) is { } host)
        {
            Assign(guest, host.Id, HostSources.GuestReport);
            return;
        }
        Assign(guest, null, null);
    }

    private static void Assign(Computer guest, int? hostId, string? source)
    {
        guest.HostComputerId = hostId;
        guest.HostSource = source;
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
