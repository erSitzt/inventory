using System.Text.Json;
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
        var networks = await NetworksAsync(db, ct);
        var names = await GatewayNamesAsync(db, ct);

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
        return NetworkMapBuilder.Build(mapComputers, visibleDevices, [.. ranges.Select(r => (r.Name, r.Addresses))], traces, networks, names);
    }

    /// <summary>Benennt ein Gateway ohne gescanntes Gerät; leer = wieder die Adresse.</summary>
    /// <param name="address">Die Adresse, unter der der Name gespeichert wird (eine Adresse des Gateways).</param>
    public async Task SetGatewayNameAsync(string address, string? name, CancellationToken ct = default)
    {
        if (!IpRanges.IsUsable(address))
            throw new ArgumentException("Not an IPv4 address.", nameof(address));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var names = await GatewayNamesAsync(db, ct);
        if (string.IsNullOrWhiteSpace(name))
            names.Remove(address);
        else
            names[address] = name.Trim();
        var setting = await db.Settings.FindAsync([SettingKeys.NetworkMapGatewayNames], ct);
        var value = names.Count > 0 ? JsonSerializer.Serialize(names) : null;
        if (setting is null)
            db.Settings.Add(new Setting { Key = SettingKeys.NetworkMapGatewayNames, Value = value });
        else
            setting.Value = value;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Dictionary<string, string>> GatewayNamesAsync(InventoryDbContext db, CancellationToken ct)
    {
        var stored = await db.Settings.Where(s => s.Key == SettingKeys.NetworkMapGatewayNames).Select(s => s.Value).FirstOrDefaultAsync(ct);
        try
        {
            return stored is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Die relevanten Netze; leer = alle.</summary>
    public async Task<List<string>> GetNetworksAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await NetworksAsync(db, ct);
    }

    /// <summary>Speichert die relevanten Netze; leer = alle.</summary>
    /// <exception cref="FormatException">Eine Angabe ist kein IPv4-Netz.</exception>
    public async Task SetNetworksAsync(string? text, CancellationToken ct = default)
    {
        var networks = IpRanges.ParseNetworks(text);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.FindAsync([SettingKeys.NetworkMapNetworks], ct);
        var value = networks.Count > 0 ? string.Join(",", networks) : null;
        if (setting is null)
            db.Settings.Add(new Setting { Key = SettingKeys.NetworkMapNetworks, Value = value });
        else
            setting.Value = value;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Für Dienste, die bereits einen Kontext offen haben (Gerätescan).</summary>
    internal static async Task<List<string>> NetworksAsync(InventoryDbContext db, CancellationToken ct)
    {
        var stored = await db.Settings.Where(s => s.Key == SettingKeys.NetworkMapNetworks).Select(s => s.Value).FirstOrDefaultAsync(ct);
        try
        {
            return IpRanges.ParseNetworks(stored);
        }
        catch (FormatException)
        {
            // Nur über SetNetworksAsync gespeichert; ein beschädigter Wert soll die Karte nicht leeren.
            return [];
        }
    }

    private static string[] Split(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
