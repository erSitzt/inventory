using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Lädt Rechner, Netzwerkgeräte und Bereiche und baut daraus die Netzwerkkarte (<see cref="NetworkMapBuilder"/>).</summary>
public sealed class NetworkMapService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    /// <param name="allowedComputers">Rechner, die der Benutzer sehen darf; null = alle.</param>
    public async Task<NetworkMap> BuildAsync(IReadOnlySet<int>? allowedComputers = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.IsOnline, c.ChassisType, c.Model, c.OsName, c.IpAddress, Adapters = c.NetworkAdapters.Select(a => new { a.IpAddresses, a.SubnetMasks, a.Gateways }).ToList() })
            .ToListAsync(ct);
        var devices = await db.NetworkDevices.AsNoTracking().ToListAsync(ct);
        var ranges = await db.NetworkRanges.AsNoTracking().Select(r => new { r.Name, r.Addresses }).ToListAsync(ct);
        var traces = await db.NetworkTraces.AsNoTracking().ToListAsync(ct);

        var mapComputers = computers.Where(c => allowedComputers is null || allowedComputers.Contains(c.Id)).Select(c =>
        {
            var addresses = new List<(string, int?)>();
            foreach (var adapter in c.Adapters)
            {
                var ips = Split(adapter.IpAddresses);
                var masks = Split(adapter.SubnetMasks);
                for (var i = 0; i < ips.Length; i++)
                    addresses.Add((ips[i], i < masks.Length ? IpRanges.PrefixLength(masks[i]) : null));
            }
            return new MapComputer(c.Id, c.Name, c.IsOnline, Chassis.Role(c.ChassisType, c.Model, c.OsName), addresses,
                [.. c.Adapters.SelectMany(a => Split(a.Gateways)).Distinct()], c.IpAddress);
        }).ToList();
        // Geräte, die zu einem nicht sichtbaren Rechner gehören, bleiben ebenfalls weg.
        var visibleDevices = devices.Where(d => d.ComputerId is not { } id || allowedComputers is null || allowedComputers.Contains(id)).ToList();
        return NetworkMapBuilder.Build(mapComputers, visibleDevices, [.. ranges.Select(r => (r.Name, r.Addresses))], traces);
    }

    private static string[] Split(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
