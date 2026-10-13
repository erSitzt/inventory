using Inventory.Core;
using Inventory.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Ergebnis eines Weckversuchs: abgeschickt heißt nicht aufgewacht – Wake-on-LAN kennt keine Antwort.</summary>
/// <param name="Macs">Die angesprochenen MAC-Adressen; leer, wenn keine bekannt ist.</param>
/// <param name="Packets">Abgeschickte Pakete; 0 trotz bekannter MAC-Adresse heißt, dass kein Weg ins Netz offen war.</param>
public sealed record WakeResult(int ComputerId, string ComputerName, IReadOnlyList<string> Macs, int Packets)
{
    public bool Sent => Macs.Count > 0 && Packets > 0;
}

/// <summary>Weckt Rechner per Wake-on-LAN über die MAC-Adressen ihres letzten Scans.</summary>
public sealed class WakeService(IDbContextFactory<InventoryDbContext> dbFactory, IWakeSender sender)
{
    /// <summary>So lange braucht ein Rechner nach dem Wecken etwa, bis er im Netz antwortet.</summary>
    public static readonly TimeSpan BootWait = TimeSpan.FromMinutes(2);

    public async Task<WakeResult?> WakeAsync(int computerId, CancellationToken ct = default) =>
        (await WakeAsync([computerId], ct)).FirstOrDefault();

    /// <summary>Schickt jedem der Rechner Magic Packets; Rechner ohne bekannte MAC-Adresse stehen mit leerer Liste im Ergebnis.</summary>
    public async Task<List<WakeResult>> WakeAsync(IReadOnlyCollection<int> computerIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.AsNoTracking().Where(c => computerIds.Contains(c.Id))
            .OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, c.WakeMac, c.IpAddress }).ToListAsync(ct);
        var adapters = (await db.NetworkAdapters.AsNoTracking().Where(a => computerIds.Contains(a.ComputerId))
            .Select(a => new { a.ComputerId, a.MacAddress, a.IpAddresses, a.SubnetMasks }).ToListAsync(ct)).ToLookup(a => a.ComputerId);

        var results = new List<WakeResult>();
        foreach (var computer in computers)
        {
            var plan = WakeOnLan.PlanFor(computer.WakeMac, computer.IpAddress,
                adapters[computer.Id].Select(a => new WakeOnLan.Adapter(a.MacAddress, Split(a.IpAddresses), Split(a.SubnetMasks))));
            var packets = 0;
            foreach (var mac in plan.Macs)
                packets += await sender.SendAsync(WakeOnLan.MagicPacket(mac), plan.Addresses, ct);
            results.Add(new WakeResult(computer.Id, computer.Name, [.. plan.Macs.Select(WakeOnLan.Format)], packets));
        }
        return results;
    }

    private static string[] Split(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
