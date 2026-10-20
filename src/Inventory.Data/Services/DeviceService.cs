using System.Collections.Concurrent;
using System.Net;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

public sealed record DeviceScanProgress(string Range, int Done, int Total, int Found);

/// <summary>Verwaltet die IP-Bereiche des Gerätescans, sucht sie nach Geräten ab und hält die gefundenen Geräte.</summary>
public sealed class DeviceService(IDbContextFactory<InventoryDbContext> dbFactory, IDeviceProbe probe, ISecretProtector protector,
    TimeProvider? clock = null, IStringLocalizer<SharedResource>? localizer = null)
{
    /// <summary>So viele Adressen werden gleichzeitig geprüft; jede öffnet dabei bis zu 30 kurze Verbindungen.</summary>
    private const int MaxParallelAddresses = 32;
    private static readonly string[] DefaultCommunities = ["public"];
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _scanning = new(1, 1);

    /// <summary>Bereich, der gerade gescannt wird; null = keiner.</summary>
    public DeviceScanProgress? Current { get; private set; }
    public event Action? ProgressChanged;

    public async Task<List<NetworkDevice>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var devices = await db.NetworkDevices.AsNoTracking().Include(d => d.Computer).Include(d => d.NetworkRange).Include(d => d.Supplies).ToListAsync(ct);
        return [.. devices.OrderBy(d => IpRanges.SortKey(d.IpAddress))];
    }

    public async Task<NetworkDevice?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.NetworkDevices.AsNoTracking().Include(d => d.Computer).Include(d => d.NetworkRange).Include(d => d.Supplies)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    /// <summary>Name, Art und Notizen, die der Benutzer festlegt; leere Werte heben die Festlegung auf.</summary>
    public async Task UpdateAsync(int id, string? name, DeviceKind? kind, string? notes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var device = await db.NetworkDevices.FindAsync([id], ct) ?? throw new KeyNotFoundException(L["Device {0} not found", id]);
        device.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        device.KindOverride = kind;
        device.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.NetworkDevices.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<List<NetworkRange>> ListRangesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.NetworkRanges.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
    }

    /// <param name="communities">null = unverändert lassen; leer = die allgemeine Einstellung verwenden; sonst eine je Zeile.</param>
    public async Task<NetworkRange> SaveRangeAsync(int? id, string name, string addresses, bool enabled, string? communities, string? notes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(L["A name is required."], nameof(name));
        if (IpRanges.Validate(addresses) is { } error)
            throw new ArgumentException(L["Invalid addresses: {0}", error], nameof(addresses));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        NetworkRange range;
        if (id is null)
        {
            range = new NetworkRange { Name = name.Trim(), Addresses = addresses.Trim() };
            db.NetworkRanges.Add(range);
        }
        else
        {
            range = await db.NetworkRanges.FindAsync([id.Value], ct) ?? throw new KeyNotFoundException(L["Range {0} not found", id]);
            range.Name = name.Trim();
            range.Addresses = addresses.Trim();
        }
        if (await db.NetworkRanges.AnyAsync(r => r.Name == range.Name && r.Id != range.Id, ct))
            throw new InvalidOperationException(L["A range named \"{0}\" already exists.", range.Name]);
        range.Enabled = enabled;
        range.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (communities is not null)
            range.ProtectedCommunities = Lines(communities) is { Count: > 0 } list ? protector.Protect(string.Join("\n", list)) : null;
        await db.SaveChangesAsync(ct);
        return range;
    }

    public async Task DeleteRangeAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Die Geräte bleiben; ihr Bereich wird leer (SetNull).
        var range = await db.NetworkRanges.FindAsync([id], ct);
        if (range is null)
            return;
        db.NetworkRanges.Remove(range);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True, wenn eigene Communities gespeichert sind (sonst gilt "public").</summary>
    public async Task<bool> HasDefaultCommunitiesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.AnyAsync(s => s.Key == SettingKeys.DeviceSnmpCommunities && s.Value != null, ct);
    }

    /// <summary>Speichert die allgemeinen Communities; leer = wieder "public".</summary>
    public async Task SetDefaultCommunitiesAsync(string? communities, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.FindAsync([SettingKeys.DeviceSnmpCommunities], ct);
        var list = Lines(communities ?? "");
        var value = list.Count > 0 ? protector.Protect(string.Join("\n", list)) : null;
        if (setting is null)
            db.Settings.Add(new Setting { Key = SettingKeys.DeviceSnmpCommunities, Value = value });
        else
            setting.Value = value;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sucht einen Bereich (oder alle eingeschalteten) ab und gibt eine Zusammenfassung zurück. Läuft schon ein Scan, wird nichts gestartet.</summary>
    public async Task<string> ScanAsync(int? rangeId = null, CancellationToken ct = default)
    {
        if (!await _scanning.WaitAsync(0, ct))
            return L["A device scan is already running."];
        try
        {
            List<NetworkRange> ranges;
            string[] defaults;
            List<string> gateways;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                ranges = await db.NetworkRanges.AsNoTracking().Where(r => rangeId == null ? r.Enabled : r.Id == rangeId).OrderBy(r => r.Name).ToListAsync(ct);
                var stored = await db.Settings.Where(s => s.Key == SettingKeys.DeviceSnmpCommunities).Select(s => s.Value).FirstOrDefaultAsync(ct);
                defaults = stored is null ? DefaultCommunities : [.. Lines(protector.Unprotect(stored))];
                // Beim Scan aller Bereiche kommen die Standard-Gateways der Rechner dazu: erst sie zeigen, welche Router die Netze verbinden.
                gateways = rangeId is not null ? [] : (await db.NetworkAdapters.AsNoTracking().Where(a => a.Gateways != null).Select(a => a.Gateways!).ToListAsync(ct))
                    .SelectMany(g => g.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(IpRanges.IsUsable).Distinct().ToList();
                // Heimrouter im Homeoffice gehören nicht dazu, wenn die relevanten Netze festgelegt sind.
                var networks = await NetworkMapService.NetworksAsync(db, ct);
                gateways = [.. gateways.Where(g => IpRanges.InAny(networks, g))];
            }
            if (ranges.Count == 0 && gateways.Count == 0)
                return L["No address ranges to scan."];

            var results = new List<string>();
            var covered = new HashSet<string>();
            foreach (var range in ranges)
            {
                results.Add(await ScanRangeAsync(range, defaults, ct));
                try
                {
                    covered.UnionWith(IpRanges.Expand(range.Addresses).Select(a => a.ToString()));
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    // Schon im Ergebnis des Bereichs gemeldet.
                }
            }
            var extra = gateways.Where(g => !covered.Contains(g)).Select(IPAddress.Parse).ToList();
            if (extra.Count > 0)
            {
                var found = await ProbeAllAsync(L["Gateways"], extra, defaults, ct);
                await StoreAsync(null, found, ct);
                results.Add(L["Gateways of the computers: {0} of {1} responded.", found.Count, extra.Count]);
            }
            if (rangeId is null)
                results.Add(L["Paths to {0} subnets traced.", await TraceAsync(ct)]);
            return string.Join(" ", results);
        }
        finally
        {
            Current = null;
            ProgressChanged?.Invoke();
            _scanning.Release();
        }
    }

    private async Task<string> ScanRangeAsync(NetworkRange range, string[] defaults, CancellationToken ct)
    {
        List<IPAddress> addresses;
        try
        {
            addresses = IpRanges.Expand(range.Addresses);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return await FinishRangeAsync(range.Id, L["{0}: invalid addresses ({1})", range.Name, ex.Message], ct);
        }
        var communities = range.ProtectedCommunities is null ? defaults : [.. Lines(protector.Unprotect(range.ProtectedCommunities))];

        var found = await ProbeAllAsync(range.Name, addresses, communities, ct);
        var added = await StoreAsync(range.Id, found, ct);
        return await FinishRangeAsync(range.Id,
            L["{0}: {1} of {2} addresses responded, {3} new.", range.Name, found.Count, addresses.Count, added], ct);
    }

    /// <summary>Misst nur die Netzwege (für die Netzwerkkarte); läuft schon ein Scan, wird nichts gestartet.</summary>
    public async Task<string> TracePathsAsync(CancellationToken ct = default)
    {
        if (!await _scanning.WaitAsync(0, ct))
            return L["A device scan is already running."];
        try
        {
            return L["Paths to {0} subnets traced.", await TraceAsync(ct)];
        }
        finally
        {
            Current = null;
            ProgressChanged?.Invoke();
            _scanning.Release();
        }
    }

    /// <summary>
    /// Traceroute zu je einem Ziel in jedem bekannten Subnetz: bevorzugt ein antwortendes Gerät oder ein erreichbarer Rechner,
    /// sonst das Gateway. Ersetzt die Wege der letzten Messung; liefert die Zahl der gemessenen Subnetze.
    /// </summary>
    internal async Task<int> TraceAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var adapters = await db.NetworkAdapters.AsNoTracking().Select(a => new { a.IpAddresses, a.SubnetMasks, a.Gateways, Online = db.Computers.Any(c => c.Id == a.ComputerId && c.IsOnline) }).ToListAsync(ct);
        var devices = await db.NetworkDevices.AsNoTracking().Select(d => new { d.IpAddress, d.InterfaceAddresses, d.DetectedKind, d.PingReply }).ToListAsync(ct);
        var ranges = await db.NetworkRanges.AsNoTracking().Where(r => r.Enabled).Select(r => r.Addresses).ToListAsync(ct);

        static string[] Split(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subnets = new HashSet<string>();
        var hosts = new List<(string Ip, int Rank)>();
        foreach (var adapter in adapters)
        {
            var ips = Split(adapter.IpAddresses);
            var masks = Split(adapter.SubnetMasks);
            for (var i = 0; i < ips.Length; i++)
            {
                if (!IpRanges.IsUsable(ips[i]) || i >= masks.Length || IpRanges.PrefixLength(masks[i]) is not { } prefix)
                    continue;
                subnets.Add(IpRanges.Network(ips[i], prefix)!);
                if (adapter.Online)
                    hosts.Add((ips[i], 1));
            }
            hosts.AddRange(Split(adapter.Gateways).Where(IpRanges.IsUsable).Select(g => (g, 2)));
        }
        foreach (var device in devices)
        {
            foreach (var entry in Split(device.InterfaceAddresses))
                if (entry.Split('/') is [var ip, var bits] && int.TryParse(bits, out var prefix))
                    subnets.Add(IpRanges.Network(ip, prefix)!);
            // Router antworten oft nicht von ihrer gescannten Adresse; als Ziel taugen andere Geräte besser.
            hosts.Add((device.IpAddress, device.DetectedKind is DeviceKind.Router or DeviceKind.Firewall ? 2 : device.PingReply ? 0 : 1));
        }
        foreach (var token in ranges.SelectMany(r => r.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))
            if (token.Split('/') is [var ip, var bits] && int.TryParse(bits, out var prefix))
                subnets.Add(IpRanges.Network(ip, prefix)!);

        // Transfernetze (/29 und kleiner) liegen ohnehin auf dem Weg; höchstens 64 Netze je Messung, nur in den relevanten Netzen.
        var networks = await NetworkMapService.NetworksAsync(db, ct);
        var targets = subnets.Where(s => IpRanges.PrefixOf(s) is >= 8 and < 29 && IpRanges.InAny(networks, s))
            .Select(s => hosts.Where(h => IpRanges.Contains(s, h.Ip)).OrderBy(h => h.Rank).Select(h => h.Ip).FirstOrDefault())
            .Where(t => t is not null).Distinct().Take(64).ToList();
        if (targets.Count == 0)
            return 0;

        var traces = new ConcurrentBag<NetworkTrace>();
        Report(new DeviceScanProgress(L["Network paths"], 0, targets.Count, 0));
        var done = 0;
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (target, token) =>
        {
            var result = await probe.TraceAsync(IPAddress.Parse(target!), token);
            if (result.Reached || result.Hops.Any(h => h is not null))
                traces.Add(new NetworkTrace
                {
                    Target = target!, Reached = result.Reached, TracedUtc = _clock.GetUtcNow().UtcDateTime,
                    Hops = string.Join(",", result.Hops.Select(h => h ?? "*")),
                });
            Report(new DeviceScanProgress(L["Network paths"], Interlocked.Increment(ref done), targets.Count, traces.Count));
        });
        await db.NetworkTraces.ExecuteDeleteAsync(ct);
        db.NetworkTraces.AddRange(traces);
        // Mit den Wegen die eigenen Adressen: erst sie sagen sicher, in welchem Netz der Server steht.
        var own = string.Join(",", probe.LocalAddresses().Select(a => $"{a.Ip}/{a.Prefix}"));
        if (await db.Settings.FindAsync([SettingKeys.NetworkMapServerAddresses], ct) is { } setting)
            setting.Value = own;
        else
            db.Settings.Add(new Setting { Key = SettingKeys.NetworkMapServerAddresses, Value = own });
        await db.SaveChangesAsync(ct);
        return traces.Count;
    }

    /// <summary>Prüft die Adressen parallel; MAC-Adressen kommen aus der ARP-Tabelle des Servers (gleiches Segment) oder eines gefundenen Routers.</summary>
    private async Task<List<DeviceObservation>> ProbeAllAsync(string label, IReadOnlyList<IPAddress> addresses, IReadOnlyList<string> communities, CancellationToken ct)
    {
        var found = new ConcurrentBag<DeviceObservation>();
        var done = 0;
        Report(new DeviceScanProgress(label, 0, addresses.Count, 0));
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelAddresses, CancellationToken = ct }, async (address, token) =>
        {
            var observation = await probe.ProbeAsync(address, communities, token);
            if (observation.Responded)
                found.Add(observation);
            var count = Interlocked.Increment(ref done);
            if (count % 8 == 0 || count == addresses.Count)
                Report(new DeviceScanProgress(label, count, addresses.Count, found.Count));
        });

        var macs = new Dictionary<string, string>(probe.LocalNeighbors());
        foreach (var router in found.Where(o => o.Snmp?.ArpTable.Count > 0))
            foreach (var (ip, mac) in router.Snmp!.ArpTable)
                macs.TryAdd(ip, mac);
        foreach (var observation in found)
            observation.MacAddress ??= macs.GetValueOrDefault(observation.IpAddress);
        return [.. found];
    }

    private void Report(DeviceScanProgress progress)
    {
        Current = progress;
        ProgressChanged?.Invoke();
    }

    private async Task<string> FinishRangeAsync(int rangeId, string result, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.NetworkRanges.Where(r => r.Id == rangeId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.LastScanUtc, _clock.GetUtcNow().UtcDateTime)
            .SetProperty(r => r.LastResult, result), ct);
        return result;
    }

    /// <summary>Übernimmt die Beobachtungen; liefert die Zahl der neu angelegten Geräte.</summary>
    /// <summary>
    /// Übernimmt Windows-Geräte ohne Rechner in die Rechnerliste: unter dem Namen, den sie über SMB nennen, sonst dem DNS-Namen,
    /// sonst der Adresse. Gibt es den Rechner schon, wird das Gerät nur verknüpft.
    /// </summary>
    public async Task<DeviceAdoption> AdoptAsync(IReadOnlyCollection<int> deviceIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var devices = (await db.NetworkDevices.Where(d => deviceIds.Contains(d.Id) && d.ComputerId == null).ToListAsync(ct)).Where(d => d.CanAdopt).ToList();
        var computers = await db.Computers.ToListAsync(ct);
        var added = new List<Computer>();
        var linked = new List<string>();
        foreach (var device in devices.OrderBy(d => IpRanges.SortKey(d.IpAddress)))
        {
            var name = new[] { device.WindowsName, device.ReverseName, device.IpAddress }.FirstOrDefault(n => n is not null && ComputerNames.IsValid(n))!;
            var computer = computers.FirstOrDefault(c => ShortName(c.Name).Equals(ShortName(name), StringComparison.OrdinalIgnoreCase));
            if (computer is null)
            {
                // Der DNS-Name aus dem Reverse-Lookup macht einen kurzen SMB-Namen auch über Domänengrenzen und VPN erreichbar.
                computer = new Computer { Name = name, IpAddress = device.IpAddress, DnsName = MachineNames.ValidDnsName(device.ReverseName, name) };
                db.Computers.Add(computer);
                computers.Add(computer);
                added.Add(computer);
            }
            else if (!added.Contains(computer))
            {
                linked.Add($"{device.IpAddress} → {computer.Name}");
            }
            device.Computer = computer;
        }
        await db.SaveChangesAsync(ct);
        return new DeviceAdoption([.. added.Select(c => c.Id)], [.. added.Select(c => c.Name)], linked);
    }

    private static string ShortName(string name) => IPAddress.TryParse(name, out _) ? name : name.Split('.')[0];

    internal async Task<int> StoreAsync(int? rangeId, IReadOnlyList<DeviceObservation> observations, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var devices = await db.NetworkDevices.Include(d => d.Supplies).ToListAsync(ct);
        var byIp = devices.ToDictionary(d => d.IpAddress);
        var computers = await db.Computers.AsNoTracking().Select(c => new { c.Id, c.Name, c.IpAddress }).ToListAsync(ct);
        var added = 0;

        foreach (var o in observations.OrderBy(o => IpRanges.SortKey(o.IpAddress)))
        {
            var mac = WakeOnLan.NormalizeMac(o.MacAddress);
            if (!byIp.TryGetValue(o.IpAddress, out var device))
            {
                // Ein bekanntes Gerät mit neuer Adresse (DHCP) zieht um, statt doppelt aufzutauchen – aber nur innerhalb desselben
                // Bereichs und nur, wenn es unter seiner alten Adresse nicht mehr antwortet. Sonst wären Geräte mit mehreren Adressen
                // oder Router mit Proxy-ARP (eine MAC für viele Adressen) ein ständiges Hin und Her.
                device = mac is null || rangeId is null ? null
                    : devices.FirstOrDefault(d => d.MacAddress == mac && d.NetworkRangeId == rangeId && !observations.Any(x => x.IpAddress == d.IpAddress));
                if (device is null)
                {
                    device = new NetworkDevice { IpAddress = o.IpAddress, FirstSeenUtc = now };
                    db.NetworkDevices.Add(device);
                    devices.Add(device);
                    added++;
                }
                else
                {
                    byIp.Remove(device.IpAddress);
                    device.IpAddress = o.IpAddress;
                }
                byIp[o.IpAddress] = device;
            }
            Apply(device, o, mac, now);
            device.NetworkRangeId = rangeId ?? device.NetworkRangeId;

            // Ein inventarisierter Rechner: über den Namen, den er selbst nennt, sonst über seine zuletzt gemeldete Adresse.
            var computer = (o.WindowsName is { } windowsName ? computers.FirstOrDefault(c => c.Name.Split('.')[0].Equals(windowsName, StringComparison.OrdinalIgnoreCase)) : null)
                ?? (device.Kind == DeviceKind.Windows ? computers.FirstOrDefault(c => c.IpAddress == o.IpAddress) : null);
            device.ComputerId = computer?.Id ?? (o.WindowsName is null ? device.ComputerId : null);
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    private static void Apply(NetworkDevice device, DeviceObservation o, string? mac, DateTime now)
    {
        device.LastSeenUtc = now;
        device.PingReply = o.PingReply;
        device.OpenPorts = o.OpenPorts.Count == 0 ? null : string.Join(",", o.OpenPorts);
        if (mac is not null)
        {
            device.MacAddress = mac;
            device.MacVendor = MacVendors.Lookup(mac);
        }
        device.ReverseName = o.ReverseName ?? device.ReverseName;
        device.WindowsName = o.WindowsName ?? device.WindowsName;
        device.SshBanner = o.SshBanner ?? device.SshBanner;
        device.Banner = o.Banner ?? device.Banner;
        device.HttpServer = o.HttpServer ?? device.HttpServer;
        device.HttpTitle = o.HttpTitle ?? device.HttpTitle;
        device.TlsSubject = o.TlsSubject ?? device.TlsSubject;
        device.DiscoveredName = o.Snmp?.SysName ?? o.WindowsName ?? o.ReverseName?.Split('.')[0] ?? device.DiscoveredName;

        // Antwortet SNMP diesmal nicht (UDP geht verloren, Gerät beschäftigt), bleibt die Einordnung vom letzten Mal.
        if (o.Snmp is null && device.SnmpResponded)
            return;
        var snmp = o.Snmp;
        var classification = DeviceClassifier.Classify(o, device.MacVendor);
        device.DetectedKind = classification.Kind;
        device.KindReason = string.IsNullOrEmpty(classification.Reason) ? null : classification.Reason;
        device.Manufacturer = classification.Manufacturer ?? device.Manufacturer;
        device.Model = classification.Model ?? device.Model;
        device.OsGuess = classification.Os ?? device.OsGuess;
        if (snmp is null)
            return;
        device.SnmpResponded = true;
        device.SysDescr = snmp.SysDescr;
        device.SysObjectId = snmp.SysObjectId;
        device.SysLocation = snmp.SysLocation;
        device.SysContact = snmp.SysContact;
        device.UptimeSeconds = snmp.UptimeSeconds;
        device.SerialNumber = snmp.SerialNumber ?? device.SerialNumber;
        device.Firmware = snmp.Firmware ?? device.Firmware;
        device.PageCount = snmp.PageCount ?? device.PageCount;
        // Leitet das Gerät nicht (mehr) weiter, sind alte Schnittstellen hinfällig; ein leerer Walk bei einem Router behält die letzten.
        device.InterfaceAddresses = snmp.InterfaceAddresses.Count > 0 ? string.Join(",", snmp.InterfaceAddresses)
            : snmp.Forwarding == false ? null : device.InterfaceAddresses;
        if (snmp.IsPrinter)
        {
            device.Supplies.Clear();
            device.Supplies.AddRange(snmp.Supplies.Select(s => new DeviceSupply { Description = s.Description, Level = s.Level, Capacity = s.Capacity }));
        }
    }

    /// <summary>Alle Geräte als CSV (Semikolon, wie die übrigen Exporte).</summary>
    /// <param name="zone">Zeitzone der Zeitangaben (die des Browsers); null = die des Servers.</param>
    public async Task<string> CsvAsync(Func<DeviceKind, string> kindName, CancellationToken ct = default, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        string Stamp(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var columns = new List<ReportColumn>
        {
            new("ip", L["IP address"]), new("name", L["Name"]), new("kind", L["Device type"]), new("manufacturer", L["Manufacturer"]), new("model", L["Model"]),
            new("serial", L["Serial number"]), new("os", L["Operating system"]), new("mac", L["MAC address"]), new("vendor", L["MAC vendor"]),
            new("ports", L["Open ports"]), new("location", L["Location"]), new("pages", L["Page count"], Numeric: true),
            new("computer", L["Computer"]), new("range", L["Address range"]), new("firstSeen", L["First seen"]), new("lastSeen", L["Last seen"]),
        };
        var rows = (await ListAsync(ct)).Select(d => new string?[]
        {
            d.IpAddress, d.DisplayName, kindName(d.Kind), d.Manufacturer, d.Model, d.SerialNumber, d.OsGuess, d.MacAddress, d.MacVendor,
            d.OpenPorts?.Replace(",", ", "), d.SysLocation, d.PageCount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            d.Computer?.Name, d.NetworkRange?.Name, Stamp(d.FirstSeenUtc), Stamp(d.LastSeenUtc),
        }).ToList();
        return ReportExport.Csv(new ReportTable(columns, rows));
    }

    private static List<string> Lines(string text) =>
        [.. text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
}

/// <param name="AddedIds">Neu angelegte Rechner.</param>
/// <param name="Linked">Geräte, die mit einem schon vorhandenen Rechner verknüpft wurden, als "Adresse → Rechner".</param>
public sealed record DeviceAdoption(List<int> AddedIds, List<string> AddedNames, List<string> Linked)
{
    /// <summary>Für das Protokoll: neue Rechner und Verknüpfungen.</summary>
    public string Details => string.Join(", ", AddedNames.Concat(Linked));
}
