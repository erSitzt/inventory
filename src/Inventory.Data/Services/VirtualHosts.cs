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
            var guest = await FindByUuidAsync(db, machine, computerId, ct);
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
            // Dieselbe UUID bei einem anderen Rechner: eine geklonte VM. Dann sagt die UUID allein nicht, welche VM welcher Rechner ist.
            var shared = await db.Computers.AnyAsync(c => c.Id != guest.Id && c.BiosUuid != null && candidates.Contains(c.BiosUuid), ct);
            var machines = await db.HostedVirtualMachines.Where(v => v.BiosUuid != null && candidates.Contains(v.BiosUuid) && v.ComputerId != guest.Id).ToListAsync(ct);
            machine = Pick(machines, v => SameName(v.Name, guest), v => v.Name, v => v.Running, shared);
            if (machine is not null)
                (source, hostComputer) = (HostSources.BiosUuid, machine.ComputerId);
            else if ((vm = Pick(await db.HypervisorVms.Where(v => v.BiosUuid != null && candidates.Contains(v.BiosUuid)).ToListAsync(ct),
                         v => Matches(v, guest), v => v.Name, v => v.PoweredOn, shared)) is not null)
                source = HostSources.Hypervisor;
        }
        if (source is null && guest.ReportedHostName is { Length: > 0 } reported && await FindByNameAsync(db, reported, guest.Id, ct) is { } host)
            (source, hostComputer) = (HostSources.GuestReport, host.Id);
        // Ohne UUID-Treffer: eine vCenter-VM, deren Gastname oder Name zum Rechner passt, und die keinem anderen Rechner gehört.
        if (source is null && guest.HyperVGuest != true && guest.IsVirtual)
        {
            vm = (await db.HypervisorVms.Where(v => v.GuestComputerId == null || v.GuestComputerId == guest.Id).ToListAsync(ct))
                .FirstOrDefault(v => Matches(v, guest));
            if (vm is not null)
                source = HostSources.HypervisorName;
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

    /// <summary>
    /// Unter den VMs mit der UUID eines Rechners die richtige. Die mit seinem Namen geht vor. Sonst gilt die UUID allein nur,
    /// wenn alle Treffer Kopien derselben VM sind (gleicher Name, z. B. nach einer Migration auf einen anderen Host; dann die
    /// laufende) und kein anderer Rechner dieselbe UUID trägt. Bei geklonten VMs mit kopierter BIOS-UUID wäre die Zuordnung
    /// sonst Zufall, und zwei Gäste stünden über Kreuz.
    /// </summary>
    private static T? Pick<T>(List<T> found, Func<T, bool> sameName, Func<T, string> name, Func<T, bool> running, bool shared) where T : class
    {
        var named = found.Where(sameName).ToList();
        var pool = named.Count > 0 ? named
            : !shared && found.Select(v => NameKey(name(v))).Distinct().Count() <= 1 ? found
            : [];
        return pool.OrderByDescending(running).FirstOrDefault();
    }

    /// <summary>Gleicher Schlüssel für eine UUID und ihre Schreibweise mit vertauschter Bytefolge.</summary>
    private static string UuidKey(string uuid) =>
        BiosUuids.Normalize(uuid) is { } normalized && BiosUuids.Swapped(normalized) is var swapped
            ? string.CompareOrdinal(normalized, swapped) <= 0 ? normalized : swapped
            : uuid;

    /// <summary>Der Name einer Hyper-V-VM passt zum Rechner: zu seinem Namen, DNS-Namen oder dem Namen, den er selbst meldet.</summary>
    private static bool SameName(string vmName, Computer computer) =>
        !string.IsNullOrWhiteSpace(vmName)
        && (string.Equals(vmName.Trim(), computer.DnsName, StringComparison.OrdinalIgnoreCase)
            || NameKey(vmName) == NameKey(computer.Name)
            || computer.DnsName is not null && NameKey(vmName) == NameKey(computer.DnsName)
            || computer.ReportedName is not null && NameKey(vmName) == NameKey(computer.ReportedName));

    /// <summary>Kurzname in Großbuchstaben: "vm-sql01.corp.example" und "VM-SQL01" ergeben denselben Schlüssel.</summary>
    private static string NameKey(string name) => name.Trim().Split('.')[0].ToUpperInvariant();

    private static bool Matches(HypervisorVm vm, Computer computer) =>
        new[] { vm.GuestHostName, vm.Name }.Any(n => !string.IsNullOrWhiteSpace(n)
            && (string.Equals(n.Trim(), computer.DnsName, StringComparison.OrdinalIgnoreCase)
                || NameKey(n) == NameKey(computer.Name)
                || computer.DnsName is not null && NameKey(n) == NameKey(computer.DnsName)
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
        var byUuid = vms.Where(v => v.BiosUuid is not null).ToLookup(v => v.BiosUuid!);
        // UUIDs, die mehrere Rechner tragen (geklonte VMs): dort entscheidet der Name.
        var shared = (await db.Computers.Where(c => c.BiosUuid != null).Select(c => c.BiosUuid!).ToListAsync(ct))
            .GroupBy(UuidKey).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
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
            var found = byUuid[uuid].Concat(byUuid[BiosUuids.Swapped(uuid)]).Distinct().Where(v => !claimed.Contains(v)).ToList();
            if (Pick(found, v => Matches(v, computer), v => v.Name, v => v.PoweredOn, shared.Contains(UuidKey(uuid))) is not { } vm || !claimed.Add(vm))
                continue;
            Link(computer, vm, HostSources.Hypervisor);
            pending.Remove(computer);
        }
        var unclaimed = vms.Where(v => !claimed.Contains(v)).ToList();
        foreach (var computer in pending)
        {
            var vm = computer.HyperVGuest == true ? null : unclaimed.FirstOrDefault(v => Matches(v, computer));
            if (vm is not null)
            {
                unclaimed.Remove(vm);
                Link(computer, vm, HostSources.HypervisorName);
            }
            else if (computer.HostSource is HostSources.Hypervisor or HostSources.HypervisorName || computer.HypervisorHostId is not null)
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

    /// <summary>
    /// Der inventarisierte Gast einer Hyper-V-VM über die UUID. Tragen mehrere Rechner sie oder mehrere unterschiedlich
    /// benannte VMs (geklont), entscheidet der Name.
    /// </summary>
    private static async Task<Computer?> FindByUuidAsync(InventoryDbContext db, HostedVirtualMachine machine, int hostId, CancellationToken ct)
    {
        if (BiosUuids.Normalize(machine.BiosUuid) is not { } normalized)
            return null;
        var candidates = new[] { normalized, BiosUuids.Swapped(normalized) };
        var computers = await db.Computers.Where(c => c.Id != hostId && c.BiosUuid != null && candidates.Contains(c.BiosUuid)).ToListAsync(ct);
        if (computers.FirstOrDefault(c => SameName(machine.Name, c)) is { } named)
            return named;
        if (computers.Count != 1)
            return null;
        var otherNames = await db.HostedVirtualMachines.Where(v => v.Id != machine.Id && v.BiosUuid != null && candidates.Contains(v.BiosUuid))
            .Select(v => v.Name).ToListAsync(ct);
        return otherNames.All(n => NameKey(n) == NameKey(machine.Name)) ? computers[0] : null;
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
