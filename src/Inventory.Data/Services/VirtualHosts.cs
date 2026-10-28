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
            // Wie beim Scan des Gasts: Hyper-V über die UUID geht vor, ein Verweis aus dem vCenter wird dabei gelöst.
            await ResolveGuestAsync(db, guest, ct);
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
    /// Verweise einer anderen Quelle auf denselben Gast werden dabei gelöst, damit er nicht unter zwei Hosts erscheint.
    /// </summary>
    internal static async Task ResolveGuestAsync(InventoryDbContext db, Computer guest, CancellationToken ct)
    {
        var candidates = guest.BiosUuid is { } uuid ? new[] { uuid, BiosUuids.Swapped(uuid) } : [];
        HostedVirtualMachine? machine = null;
        HypervisorVm? vm = null;
        string? source = null;
        int? hostComputer = null;
        if (candidates.Length > 0)
        {
            machine = await db.HostedVirtualMachines.Where(v => v.BiosUuid != null && candidates.Contains(v.BiosUuid) && v.ComputerId != guest.Id)
                .OrderByDescending(v => v.Running).FirstOrDefaultAsync(ct);
            if (machine is not null)
                (source, hostComputer) = (HostSources.BiosUuid, machine.ComputerId);
            else if ((vm = await db.HypervisorVms.Where(v => v.BiosUuid != null && candidates.Contains(v.BiosUuid)).OrderByDescending(v => v.PoweredOn).FirstOrDefaultAsync(ct)) is not null)
                source = HostSources.VCenter;
        }
        if (source is null && guest.ReportedHostName is { Length: > 0 } reported && await FindByNameAsync(db, reported, guest.Id, ct) is { } host)
            (source, hostComputer) = (HostSources.GuestReport, host.Id);
        // Ohne UUID-Treffer: eine vCenter-VM, deren Gastname oder Name zum Rechner passt, und die keinem anderen Rechner gehört.
        if (source is null && guest.HyperVGuest != true && guest.IsVirtual)
        {
            vm = (await db.HypervisorVms.Where(v => v.GuestComputerId == null || v.GuestComputerId == guest.Id).ToListAsync(ct))
                .FirstOrDefault(v => Matches(v, guest));
            if (vm is not null)
                source = HostSources.VCenterName;
        }

        // Verweise, die nicht mehr gelten, lösen.
        foreach (var stale in await db.HostedVirtualMachines.Where(v => v.GuestComputerId == guest.Id).ToListAsync(ct))
            if (stale != machine)
                stale.GuestComputerId = null;
        foreach (var stale in await db.HypervisorVms.Where(v => v.GuestComputerId == guest.Id).ToListAsync(ct))
            if (stale != vm)
                stale.GuestComputerId = null;
        if (machine is not null)
            machine.GuestComputerId = guest.Id;
        if (vm is not null)
            vm.GuestComputerId = guest.Id;
        guest.HostComputerId = hostComputer;
        guest.HypervisorHostId = vm?.HostId;
        guest.HostSource = source;
    }

    /// <summary>Kurzname in Großbuchstaben: "vm-sql01.corp.example" und "VM-SQL01" ergeben denselben Schlüssel.</summary>
    private static string NameKey(string name) => name.Trim().Split('.')[0].ToUpperInvariant();

    private static bool Matches(HypervisorVm vm, Computer computer) =>
        new[] { vm.GuestHostName, vm.Name }.Any(n => !string.IsNullOrWhiteSpace(n)
            && (string.Equals(n.Trim(), computer.DnsName, StringComparison.OrdinalIgnoreCase)
                || NameKey(n) == NameKey(computer.Name)
                || computer.ReportedName is not null && NameKey(n) == NameKey(computer.ReportedName)));

    /// <summary>
    /// Nach einem Abgleich mit dem vCenter: jede VM mit ihrem inventarisierten Gast verbinden und Rechner neu zuordnen. Rechner
    /// mit Hyper-V-Zuordnung behalten diese. Alles im Speicher und mit einem Speichern am Ende: die VMs und die in Frage
    /// kommenden Rechner werden je einmal geladen.
    /// </summary>
    public static async Task ResolveVCenterAsync(InventoryDbContext db, CancellationToken ct = default)
    {
        var vms = await db.HypervisorVms.ToListAsync(ct);
        foreach (var vm in vms)
            vm.GuestComputerId = null;
        var byUuid = new Dictionary<string, HypervisorVm>();
        foreach (var vm in vms.Where(v => v.BiosUuid is not null).OrderBy(v => v.PoweredOn))
            byUuid[vm.BiosUuid!] = vm;
        var computers = (await db.Computers.Where(c => c.LicensingScanUtc != null || c.HypervisorHostId != null || c.HostSource != null).ToListAsync(ct))
            // Physische Rechner haben keinen Host; nur VMs und bisher zugeordnete Rechner prüfen.
            .Where(c => c.IsVirtual || c.HypervisorHostId is not null || c.HostSource is not null)
            .ToList();
        // Hyper-V geht vor: diese Gäste behalten ihren Host, ihre Verweise ins vCenter entfallen.
        var hyperV = computers.Where(c => c.HostSource is HostSources.BiosUuid or HostSources.GuestReport).ToList();
        var claimed = new HashSet<HypervisorVm>();
        var pending = computers.Except(hyperV).ToList();
        // Erst alle UUID-Treffer, damit ein Namenstreffer keinem Rechner die VM wegnimmt, die einem anderen über die UUID gehört.
        foreach (var computer in pending.ToList())
        {
            if (computer.BiosUuid is not { } uuid)
                continue;
            if (!byUuid.TryGetValue(uuid, out var vm) && !byUuid.TryGetValue(BiosUuids.Swapped(uuid), out vm) || !claimed.Add(vm))
                continue;
            Link(computer, vm, HostSources.VCenter);
            pending.Remove(computer);
        }
        var unclaimed = vms.Where(v => !claimed.Contains(v)).ToList();
        foreach (var computer in pending)
        {
            var vm = computer.HyperVGuest == true ? null : unclaimed.FirstOrDefault(v => Matches(v, computer));
            if (vm is not null)
            {
                unclaimed.Remove(vm);
                Link(computer, vm, HostSources.VCenterName);
            }
            else if (computer.HostSource is HostSources.VCenter or HostSources.VCenterName || computer.HypervisorHostId is not null)
            {
                // Die bisherige vCenter-VM gibt es nicht mehr; vielleicht kennt ein Hyper-V-Host den Rechner.
                computer.HypervisorHostId = null;
                computer.HostSource = null;
                await ResolveGuestAsync(db, computer, ct);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static void Link(Computer computer, HypervisorVm vm, string source)
    {
        vm.GuestComputerId = computer.Id;
        computer.HypervisorHostId = vm.HostId;
        computer.HostComputerId = null;
        computer.HostSource = source;
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
