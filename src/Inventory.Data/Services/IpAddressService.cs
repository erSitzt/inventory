using System.Net;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

public sealed record IpScanProgress(string Subnet, int Done, int Total, int Answered);

/// <summary>Eine Adresse des Subnetzes mit ihrem Zustand und den Hinweisen, aus denen er folgt.</summary>
public sealed record IpAddressEntry(string Address, uint Value, IpAddressStatus Status, IReadOnlyList<IpEvidence> Evidence, IpReservation? Reservation);

public sealed record IpSubnetView(IpSubnet Subnet, IReadOnlyList<IpAddressEntry> Addresses)
{
    public int Count(IpAddressStatus status) => Addresses.Count(a => a.Status == status);
}

/// <param name="Hosts">So viele bekannte Adressen liegen darin.</param>
public sealed record IpSubnetSuggestion(string Cidr, string Source, int Hosts);

/// <param name="Answered">Kandidaten, die bei der Nachprüfung doch geantwortet haben und deshalb übersprungen wurden.</param>
public sealed record IpFreeResult(IReadOnlyList<string> Addresses, IReadOnlyList<string> Answered);

/// <summary>
/// Verwaltet die Subnetze der IP-Verwaltung: prüft ihre Adressen (Ping, ARP, DNS aus dem AD oder über den Resolver), führt
/// die Hinweise mit dem Inventar zusammen, sucht freie Adressen und hält Reservierungen.
/// </summary>
public sealed class IpAddressService(IDbContextFactory<InventoryDbContext> dbFactory, IAddressProbe probe, IDnsZoneSource dns, SettingsService settings,
    CredentialService credentials, TimeProvider? clock = null, IStringLocalizer<SharedResource>? localizer = null)
{
    public const int MinPrefix = 20;
    public const int MaxPrefix = 30;
    public const int MaxPick = 256;
    private const int MaxParallel = 64;
    /// <summary>So oft wird nachgelegt, wenn bei der Nachprüfung Kandidaten antworten.</summary>
    private const int PickRounds = 4;
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _scanning = new(1, 1);
    private readonly SemaphoreSlim _reserving = new(1, 1);

    /// <summary>Subnetz, das gerade geprüft wird; null = keins.</summary>
    public IpScanProgress? Current { get; private set; }
    public event Action? ProgressChanged;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<List<IpSubnet>> ListSubnetsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return [.. (await db.IpSubnets.AsNoTracking().ToListAsync(ct)).OrderBy(s => IpRanges.SortKey(s.Cidr.Split('/')[0]))];
    }

    public async Task<IpSubnet?> GetSubnetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IpSubnets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>Netz als CIDR auf der Netzadresse ("10.0.1.7/24" → "10.0.1.0/24").</summary>
    /// <exception cref="ArgumentException">Kein IPv4-Netz oder ein Präfix außerhalb von /20–/30.</exception>
    public string NormalizeCidr(string? cidr)
    {
        var parts = (cidr ?? "").Trim().Split('/');
        if (parts.Length != 2 || IpRanges.ValueOf(parts[0]) is null || !int.TryParse(parts[1], out var prefix))
            throw new ArgumentException(L["Enter the network as CIDR, e.g. 10.0.1.0/24."], nameof(cidr));
        if (prefix is < MinPrefix or > MaxPrefix)
            throw new ArgumentException(L["The prefix must be between /{0} and /{1}.", MinPrefix, MaxPrefix], nameof(cidr));
        return IpRanges.Network(parts[0], prefix)!;
    }

    public async Task<IpSubnet> SaveSubnetAsync(int? id, string name, string cidr, int? vlan, string? gateway, string? excluded, string? notes,
        bool scanEnabled, int scanIntervalHours, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(L["A name is required."], nameof(name));
        var network = NormalizeCidr(cidr);
        gateway = string.IsNullOrWhiteSpace(gateway) ? null : gateway.Trim();
        if (gateway is not null && !IpRanges.Contains(network, gateway))
            throw new ArgumentException(L["The gateway must be an address in {0}.", network], nameof(gateway));
        excluded = string.IsNullOrWhiteSpace(excluded) ? null : excluded.Trim();
        if (excluded is not null && IpRanges.Validate(excluded) is { } error)
            throw new ArgumentException(L["Invalid excluded addresses: {0}", error], nameof(excluded));
        if (vlan is < 1 or > 4094)
            throw new ArgumentException(L["The VLAN must be between 1 and 4094."], nameof(vlan));
        if (scanIntervalHours is < 1 or > 168)
            throw new ArgumentException(L["The interval must be between 1 and 168 hours."], nameof(scanIntervalHours));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IpSubnet subnet;
        if (id is null)
        {
            subnet = new IpSubnet { Name = name.Trim(), Cidr = network, CreatedUtc = Now };
            db.IpSubnets.Add(subnet);
        }
        else
        {
            subnet = await db.IpSubnets.FindAsync([id.Value], ct) ?? throw new KeyNotFoundException(L["Subnet {0} not found", id]);
            if (subnet.Cidr != network)
            {
                // Ein anderes Netz: alte Beobachtungen gelten nicht mehr, Reservierungen nur, soweit sie im neuen liegen.
                await db.IpObservations.Where(o => o.SubnetId == subnet.Id).ExecuteDeleteAsync(ct);
                var outside = (await db.IpReservations.Where(r => r.SubnetId == subnet.Id).ToListAsync(ct)).Where(r => !IpRanges.Contains(network, r.Address));
                db.IpReservations.RemoveRange(outside);
                subnet.LastScanUtc = null;
                subnet.LastResult = null;
            }
            subnet.Name = name.Trim();
            subnet.Cidr = network;
        }
        var others = await db.IpSubnets.AsNoTracking().Where(s => s.Id != subnet.Id).ToListAsync(ct);
        if (others.FirstOrDefault(s => s.Name.Equals(subnet.Name, StringComparison.OrdinalIgnoreCase)) is not null)
            throw new InvalidOperationException(L["A subnet named \"{0}\" already exists.", subnet.Name]);
        if (others.FirstOrDefault(s => IpRanges.Overlaps(s.Cidr, network)) is { } overlapping)
            throw new InvalidOperationException(L["The network overlaps with \"{0}\" ({1}).", overlapping.Name, overlapping.Cidr]);
        subnet.Vlan = vlan;
        subnet.Gateway = gateway;
        subnet.Excluded = excluded;
        subnet.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        subnet.ScanEnabled = scanEnabled;
        subnet.ScanIntervalHours = scanIntervalHours;
        await db.SaveChangesAsync(ct);
        return subnet;
    }

    public async Task DeleteSubnetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.IpSubnets.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Netze, die das Inventar kennt und die noch kein Subnetz sind: aus den Netzwerkkarten der Rechner, der Netzwerkkarte und den Scanbereichen.</summary>
    public async Task<List<IpSubnetSuggestion>> SuggestSubnetsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.IpSubnets.AsNoTracking().Select(s => s.Cidr).ToListAsync(ct);
        var adapters = await db.NetworkAdapters.AsNoTracking().Where(a => a.IpAddresses != null).Select(a => new { a.IpAddresses, a.SubnetMasks }).ToListAsync(ct);
        var ranges = await db.NetworkRanges.AsNoTracking().Select(r => r.Addresses).ToListAsync(ct);
        var known = await KnownAddressesAsync(db, ct);

        var found = new Dictionary<string, string>();
        void Add(string? cidr, string source)
        {
            if (cidr is not null && IpRanges.PrefixOf(cidr) is >= MinPrefix and <= MaxPrefix && !existing.Any(e => IpRanges.Overlaps(e, cidr)))
                found.TryAdd(cidr, source);
        }
        foreach (var adapter in adapters)
        {
            var ips = Split(adapter.IpAddresses);
            var masks = Split(adapter.SubnetMasks);
            for (var i = 0; i < ips.Length && i < masks.Length; i++)
                if (IpRanges.IsUsable(ips[i]) && IpRanges.PrefixLength(masks[i]) is { } prefix)
                    Add(IpRanges.Network(ips[i], prefix), L["Network adapters of the computers"]);
        }
        foreach (var network in await NetworkMapService.NetworksAsync(db, ct))
            Add(network, L["Network map"]);
        foreach (var token in ranges.SelectMany(r => r.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))
            if (token.Split('/') is [var ip, var bits] && int.TryParse(bits, out var prefix))
                Add(IpRanges.Network(ip, prefix), L["Device scan ranges"]);

        // Überlappende Vorschläge (z. B. /24 aus den Karten und /22 aus dem Scanbereich): der mit den meisten Hosts bleibt.
        var result = new List<IpSubnetSuggestion>();
        foreach (var (cidr, source) in found.Select(f => (f.Key, f.Value)).OrderByDescending(f => known.Count(k => IpRanges.Contains(f.Key, k))))
            if (!result.Any(r => IpRanges.Overlaps(r.Cidr, cidr)))
                result.Add(new IpSubnetSuggestion(cidr, source, known.Count(k => IpRanges.Contains(cidr, k))));
        return [.. result.OrderBy(r => IpRanges.SortKey(r.Cidr.Split('/')[0]))];
    }

    public async Task<IpSubnetView?> GetViewAsync(int subnetId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var subnet = await db.IpSubnets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subnetId, ct);
        return subnet is null ? null : (await BuildAsync(db, [subnet], ct))[0];
    }

    /// <summary>Alle Subnetze mit allen Adressen, für die Übersicht.</summary>
    public async Task<List<IpSubnetView>> GetViewsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var subnets = (await db.IpSubnets.AsNoTracking().ToListAsync(ct)).OrderBy(s => IpRanges.SortKey(s.Cidr.Split('/')[0])).ToList();
        return await BuildAsync(db, subnets, ct);
    }

    private sealed record KnownHost(int Id, string Name, string Address, DateTime? SeenUtc);

    private async Task<List<IpSubnetView>> BuildAsync(InventoryDbContext db, List<IpSubnet> subnets, CancellationToken ct)
    {
        if (subnets.Count == 0)
            return [];
        var now = Now;
        var ids = subnets.Select(s => s.Id).ToList();
        var observations = await db.IpObservations.AsNoTracking().Where(o => ids.Contains(o.SubnetId)).ToListAsync(ct);
        var reservations = await db.IpReservations.AsNoTracking().Where(r => ids.Contains(r.SubnetId)).ToListAsync(ct);
        var (computers, devices, interfaces) = await InventoryAsync(db, ct);

        var evidence = new Dictionary<string, List<IpEvidence>>();
        void Add(string address, IpEvidence item)
        {
            if (!evidence.TryGetValue(address, out var list))
                evidence[address] = list = [];
            list.Add(item);
        }
        foreach (var computer in computers)
            Add(computer.Address, new IpEvidence(IpEvidenceKind.Computer, computer.Name, computer.SeenUtc, IpAddressRules.BySighting(computer.SeenUtc, now), computer.Id));
        foreach (var device in devices)
            Add(device.Address, new IpEvidence(IpEvidenceKind.Device, device.Name, device.SeenUtc, IpAddressRules.BySighting(device.SeenUtc, now), device.Id));
        foreach (var router in interfaces)
            Add(router.Address, new IpEvidence(IpEvidenceKind.RouterInterface, router.Name, router.SeenUtc, IpAddressRules.BySighting(router.SeenUtc, now), router.Id));
        foreach (var o in observations)
        {
            if (o.LastReplyUtc is { } reply)
                Add(o.Address, new IpEvidence(IpEvidenceKind.Ping, null, reply, IpAddressRules.BySighting(reply, now)));
            if (o.MacAddress is not null)
                Add(o.Address, new IpEvidence(IpEvidenceKind.Arp, o.MacAddress, o.MacSeenUtc, IpAddressRules.BySighting(o.MacSeenUtc, now)));
            if (o.ARecords is not null)
                Add(o.Address, new IpEvidence(IpEvidenceKind.ARecord, o.ARecords, o.ARefreshedUtc, IpAddressRules.ByDnsRecord(o.ARefreshedUtc, now)));
            if (o.PtrName is not null)
                Add(o.Address, new IpEvidence(IpEvidenceKind.PtrRecord, o.PtrName, o.PtrRefreshedUtc,
                    o.PtrConfirmed == false ? IpAddressStatus.Stale : IpAddressRules.ByDnsRecord(o.PtrRefreshedUtc, now)));
        }
        foreach (var r in reservations)
            Add(r.Address, new IpEvidence(IpEvidenceKind.Reservation, r.Note, r.CreatedUtc, IpAddressStatus.Reserved));

        var views = new List<IpSubnetView>();
        foreach (var subnet in subnets)
        {
            var excluded = ExcludedSet(subnet);
            var reserved = reservations.Where(r => r.SubnetId == subnet.Id).ToDictionary(r => r.Address);
            var entries = new List<IpAddressEntry>();
            foreach (var ip in IpRanges.Expand(subnet.Cidr))
            {
                var address = ip.ToString();
                var items = evidence.TryGetValue(address, out var list) ? new List<IpEvidence>(list) : [];
                if (address == subnet.Gateway)
                    items.Insert(0, new IpEvidence(IpEvidenceKind.Gateway, null, null, IpAddressStatus.InUse));
                if (excluded.Contains(address))
                    items.Add(new IpEvidence(IpEvidenceKind.Excluded, null, null, IpAddressStatus.Excluded));
                entries.Add(new IpAddressEntry(address, IpRanges.ValueOf(address)!.Value, IpAddressRules.Classify(items), items, reserved.GetValueOrDefault(address)));
            }
            views.Add(new IpSubnetView(subnet, entries));
        }
        return views;
    }

    private static HashSet<string> ExcludedSet(IpSubnet subnet)
    {
        try
        {
            return subnet.Excluded is null ? [] : IpRanges.Expand(subnet.Excluded).Select(a => a.ToString()).ToHashSet();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Rechner (alle Adressen ihrer Netzwerkkarten), Geräte und die eigenen Adressen der Router.</summary>
    private static async Task<(List<KnownHost> Computers, List<KnownHost> Devices, List<KnownHost> Interfaces)> InventoryAsync(InventoryDbContext db, CancellationToken ct)
    {
        var computers = await db.Computers.AsNoTracking().Select(c => new { c.Id, c.Name, c.IpAddress, c.LastSeenUtc, c.AgentLastSeenUtc }).ToListAsync(ct);
        var adapters = await db.NetworkAdapters.AsNoTracking().Where(a => a.IpAddresses != null).Select(a => new { a.ComputerId, a.IpAddresses }).ToListAsync(ct);
        var devices = await db.NetworkDevices.AsNoTracking().Select(d => new { d.Id, Name = d.Name ?? d.DiscoveredName ?? d.ReverseName ?? d.IpAddress, d.IpAddress, d.LastSeenUtc, d.InterfaceAddresses }).ToListAsync(ct);

        var byComputer = adapters.GroupBy(a => a.ComputerId).ToDictionary(g => g.Key, g => g.SelectMany(a => Split(a.IpAddresses)));
        var hosts = new List<KnownHost>();
        foreach (var c in computers)
        {
            var seen = new[] { c.LastSeenUtc, c.AgentLastSeenUtc }.Max();
            var addresses = (byComputer.GetValueOrDefault(c.Id) ?? []).Append(c.IpAddress).Where(IpRanges.IsUsable).Distinct();
            hosts.AddRange(addresses.Select(a => new KnownHost(c.Id, c.Name, a!, seen)));
        }
        var interfaces = devices.SelectMany(d => Split(d.InterfaceAddresses).Select(e => e.Split('/')[0]).Where(a => a != d.IpAddress && IpRanges.IsUsable(a))
            .Select(a => new KnownHost(d.Id, d.Name, a, d.LastSeenUtc))).ToList();
        return (hosts, [.. devices.Select(d => new KnownHost(d.Id, d.Name, d.IpAddress, d.LastSeenUtc))], interfaces);
    }

    private static async Task<List<string>> KnownAddressesAsync(InventoryDbContext db, CancellationToken ct)
    {
        var (computers, devices, interfaces) = await InventoryAsync(db, ct);
        return [.. computers.Concat(devices).Concat(interfaces).Select(h => h.Address).Distinct()];
    }

    private static string[] Split(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Prüft ein Subnetz oder alle mit eingeschalteter Prüfung. Läuft schon eine Prüfung, wird nichts gestartet.</summary>
    public async Task<string> ScanAsync(int? subnetId = null, CancellationToken ct = default)
    {
        List<IpSubnet> subnets;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            subnets = await db.IpSubnets.AsNoTracking().Where(s => subnetId == null ? s.ScanEnabled : s.Id == subnetId).ToListAsync(ct);
        return subnets.Count == 0 ? L["No subnets to scan."] : await RunAsync(subnets, ct);
    }

    /// <summary>Prüft die Subnetze, deren Intervall abgelaufen ist; null, wenn keins fällig war oder schon eine Prüfung läuft.</summary>
    public async Task<string?> ScanDueAsync(CancellationToken ct = default)
    {
        if (Current is not null)
            return null;
        var now = Now;
        List<IpSubnet> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            due = (await db.IpSubnets.AsNoTracking().Where(s => s.ScanEnabled).ToListAsync(ct))
                .Where(s => s.LastScanUtc is not { } last || now - last >= TimeSpan.FromHours(s.ScanIntervalHours)).ToList();
        return due.Count == 0 ? null : await RunAsync(due, ct);
    }

    private async Task<string> RunAsync(List<IpSubnet> subnets, CancellationToken ct)
    {
        if (!await _scanning.WaitAsync(0, ct))
            return L["An IP address scan is already running."];
        try
        {
            Current = new IpScanProgress(subnets[0].Name, 0, 0, 0);
            ProgressChanged?.Invoke();
            var (zones, dnsError) = await ReadZonesAsync(ct);
            var results = new List<string>();
            if (dnsError is not null)
                results.Add(L["DNS zones from Active Directory not readable ({0}); the resolver was used instead.", dnsError]);
            foreach (var subnet in subnets.OrderBy(s => IpRanges.SortKey(s.Cidr.Split('/')[0])))
                results.Add(await ScanSubnetAsync(subnet, zones, ct));
            return string.Join(" ", results);
        }
        finally
        {
            Current = null;
            ProgressChanged?.Invoke();
            _scanning.Release();
        }
    }

    /// <summary>DNS-Server und Zugangsdaten wie beim AD-Abgleich.</summary>
    private async Task<(DnsZoneData? Zones, string? Error)> ReadZonesAsync(CancellationToken ct)
    {
        try
        {
            var server = await settings.GetAsync(SettingKeys.AdSyncServer, ct);
            var credential = int.TryParse(await settings.GetAsync(SettingKeys.AdSyncCredentialId, ct), out var credentialId) ? await credentials.GetAsync(credentialId, ct) : null;
            return (await dns.ReadAsync(new DnsZoneQuery(server, credential), ct), null);
        }
        catch (RemoteException ex)
        {
            return (null, ex.Message);
        }
    }

    private sealed class Sighting
    {
        public bool Replied;
        public string? Ptr;
        public bool? PtrConfirmed;
        public DateTime? PtrRefreshedUtc;
    }

    private async Task<string> ScanSubnetAsync(IpSubnet subnet, DnsZoneData? zones, CancellationToken ct)
    {
        var now = Now;
        var addresses = IpRanges.Expand(subnet.Cidr);
        var sightings = addresses.ToDictionary(a => a.ToString(), _ => new Sighting());
        var aByAddress = zones?.AddressRecords.Where(r => sightings.ContainsKey(r.Address)).GroupBy(r => r.Address).ToDictionary(g => g.Key, g => g.ToList()) ?? [];
        var ptrByAddress = zones?.PointerRecords.Where(r => sightings.ContainsKey(r.Address)).GroupBy(r => r.Address).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var aByName = zones?.AddressRecords.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(r => r.Address).ToHashSet(), StringComparer.OrdinalIgnoreCase)
                      ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var done = 0;
        var answered = 0;
        Current = new IpScanProgress(subnet.Name, 0, addresses.Count, 0);
        ProgressChanged?.Invoke();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = ct }, async (ip, token) =>
        {
            var sighting = sightings[ip.ToString()];
            sighting.Replied = await probe.PingAsync(ip, token);
            if (zones is null)
                sighting.Ptr = await probe.ReverseAsync(ip, token);
            else if (ptrByAddress.TryGetValue(ip.ToString(), out var record))
                (sighting.Ptr, sighting.PtrRefreshedUtc) = (record.Name, record.RefreshedUtc);
            if (sighting.Ptr is { } name)
            {
                // Zeigt der Name noch hierher? Namen aus fremden Zonen fragt der Resolver.
                sighting.PtrConfirmed = aByName.TryGetValue(name, out var targets)
                    ? targets.Contains(ip.ToString())
                    : (await probe.ForwardAsync(name, token)).Contains(ip.ToString());
            }
            if (sighting.Replied)
                Interlocked.Increment(ref answered);
            Current = new IpScanProgress(subnet.Name, Interlocked.Increment(ref done), addresses.Count, answered);
            ProgressChanged?.Invoke();
        });
        var neighbors = probe.LocalNeighbors();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.IpObservations.Where(o => o.SubnetId == subnet.Id).ToDictionaryAsync(o => o.Address, ct);
        var withDns = 0;
        foreach (var (address, sighting) in sightings)
        {
            var a = aByAddress.GetValueOrDefault(address);
            // Ohne AD-Zonen kennt der Scan A-Einträge nur über einen bestätigten PTR.
            var aNames = a is not null ? string.Join(", ", a.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                : zones is null && sighting.PtrConfirmed == true ? sighting.Ptr : null;
            var aRefreshed = a is null || a.Any(r => r.RefreshedUtc is null) ? null : a.Max(r => r.RefreshedUtc);
            var mac = neighbors.GetValueOrDefault(address);
            if (aNames is not null || sighting.Ptr is not null)
                withDns++;

            if (!stored.TryGetValue(address, out var o))
            {
                if (!sighting.Replied && mac is null && aNames is null && sighting.Ptr is null)
                    continue;
                o = new IpObservation { SubnetId = subnet.Id, Address = address, FirstSeenUtc = now };
                db.IpObservations.Add(o);
            }
            if (sighting.Replied)
                o.LastReplyUtc = now;
            if (mac is not null)
                (o.MacAddress, o.MacSeenUtc) = (mac, now);
            (o.PtrName, o.PtrConfirmed, o.PtrRefreshedUtc) = (sighting.Ptr, sighting.Ptr is null ? null : sighting.PtrConfirmed, sighting.PtrRefreshedUtc);
            (o.ARecords, o.ARefreshedUtc) = (aNames, aNames is null ? null : aRefreshed);
            // Ohne jede Spur ist der Eintrag überflüssig.
            if (o.LastReplyUtc is null && o.MacAddress is null && o.PtrName is null && o.ARecords is null)
                db.IpObservations.Remove(o);
        }
        var result = L["{0}: {1} of {2} addresses answered, {3} with DNS entries.", subnet.Name, answered, addresses.Count, withDns].Value;
        var entity = await db.IpSubnets.FindAsync([subnet.Id], ct);
        if (entity is not null)
        {
            entity.LastScanUtc = now;
            entity.LastResult = zones is null ? $"{result} {L["DNS via the resolver."]}" : result;
        }
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Sucht freie Adressen und prüft sie gleich nach (Ping, ARP): wer antwortet, wird übersprungen und als belegt vermerkt.
    /// Leer, wenn nicht genug passen.
    /// </summary>
    /// <param name="includeStale">Auch Adressen mit nur alten Spuren (verwaister PTR, lange nicht gesehen) anbieten.</param>
    /// <param name="fromTop">Von der höchsten Adresse abwärts suchen.</param>
    public async Task<IpFreeResult> FindFreeAsync(int subnetId, int count, bool contiguous, bool includeStale, bool fromTop, CancellationToken ct = default)
    {
        if (count is < 1 or > MaxPick)
            throw new ArgumentException(L["Between 1 and {0} addresses.", MaxPick], nameof(count));
        var view = await GetViewAsync(subnetId, ct) ?? throw new KeyNotFoundException(L["Subnet {0} not found", subnetId]);
        var candidates = view.Addresses.Where(a => a.Status == IpAddressStatus.Free || (includeStale && a.Status == IpAddressStatus.Stale)).Select(a => a.Value).ToList();
        var answered = new List<string>();
        for (var round = 0; round < PickRounds; round++)
        {
            var picked = IpAddressRules.Pick(candidates, count, contiguous, fromTop);
            if (picked.Count == 0)
                break;
            var neighbors = probe.LocalNeighbors();
            var busy = new List<uint>();
            await Parallel.ForEachAsync(picked, ct, async (value, token) =>
            {
                var address = IpRanges.FromValue(value);
                if (neighbors.ContainsKey(address) || await probe.PingAsync(IPAddress.Parse(address), token))
                    lock (busy)
                        busy.Add(value);
            });
            if (busy.Count == 0)
                return new IpFreeResult([.. picked.Select(IpRanges.FromValue)], answered);
            busy.Sort();
            answered.AddRange(busy.Select(IpRanges.FromValue));
            await RecordRepliesAsync(subnetId, busy.Select(IpRanges.FromValue), ct);
            candidates.RemoveAll(busy.Contains);
        }
        return new IpFreeResult([], answered);
    }

    private async Task RecordRepliesAsync(int subnetId, IEnumerable<string> addresses, CancellationToken ct)
    {
        var now = Now;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var address in addresses)
        {
            var o = await db.IpObservations.FirstOrDefaultAsync(x => x.SubnetId == subnetId && x.Address == address, ct);
            if (o is null)
                db.IpObservations.Add(o = new IpObservation { SubnetId = subnetId, Address = address, FirstSeenUtc = now });
            o.LastReplyUtc = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<IpReservation>> ListReservationsAsync(int subnetId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return [.. (await db.IpReservations.AsNoTracking().Where(r => r.SubnetId == subnetId).ToListAsync(ct)).OrderBy(r => IpRanges.SortKey(r.Address))];
    }

    /// <summary>Reserviert Adressen des Subnetzes; alle oder keine.</summary>
    /// <exception cref="InvalidOperationException">Eine Adresse ist schon reserviert oder liegt nicht im Subnetz.</exception>
    public async Task<List<IpReservation>> ReserveAsync(int subnetId, IReadOnlyCollection<string> addresses, string? note, DateTime? expiresUtc, string? reservedBy, CancellationToken ct = default)
    {
        if (addresses.Count is < 1 or > MaxPick)
            throw new ArgumentException(L["Between 1 and {0} addresses.", MaxPick], nameof(addresses));
        if (expiresUtc is { } expires && expires <= Now)
            throw new ArgumentException(L["The expiry date must lie in the future."], nameof(expiresUtc));
        await _reserving.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var subnet = await db.IpSubnets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subnetId, ct) ?? throw new KeyNotFoundException(L["Subnet {0} not found", subnetId]);
            var usable = IpRanges.Expand(subnet.Cidr).Select(a => a.ToString()).ToHashSet();
            var list = addresses.Select(a => a.Trim()).Distinct().ToList();
            if (list.FirstOrDefault(a => !usable.Contains(a)) is { } outside)
                throw new InvalidOperationException(L["{0} is not a usable address in {1}.", outside, subnet.Cidr]);
            var taken = await db.IpReservations.AsNoTracking().Where(r => list.Contains(r.Address)).Select(r => r.Address).ToListAsync(ct);
            if (taken.Count > 0)
                throw new InvalidOperationException(L["Already reserved: {0}", string.Join(", ", taken.OrderBy(IpRanges.SortKey))]);
            var now = Now;
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            var created = list.OrderBy(IpRanges.SortKey).Select(a => new IpReservation { SubnetId = subnetId, Address = a, Note = note, ReservedBy = reservedBy, CreatedUtc = now, ExpiresUtc = expiresUtc }).ToList();
            db.IpReservations.AddRange(created);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Eine andere Instanz (API, zweites Fenster) war schneller.
                throw new InvalidOperationException(L["Already reserved: {0}", string.Join(", ", list)]);
            }
            return created;
        }
        finally
        {
            _reserving.Release();
        }
    }

    /// <summary>Sucht freie Adressen und reserviert sie in einem Zug (für Skripte über die API).</summary>
    public async Task<List<IpReservation>> ReserveFreeAsync(int subnetId, int count, bool contiguous, string? note, DateTime? expiresUtc, string? reservedBy, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var free = await FindFreeAsync(subnetId, count, contiguous, includeStale: false, fromTop: false, ct);
            if (free.Addresses.Count == 0)
                throw new InvalidOperationException(L["Not enough free addresses in this subnet."]);
            try
            {
                return await ReserveAsync(subnetId, free.Addresses, note, expiresUtc, reservedBy, ct);
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                // Inzwischen von jemand anderem reserviert: neu suchen.
            }
        }
    }

    public async Task<IpReservation?> ReleaseAsync(int reservationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reservation = await db.IpReservations.FindAsync([reservationId], ct);
        if (reservation is null)
            return null;
        db.IpReservations.Remove(reservation);
        await db.SaveChangesAsync(ct);
        return reservation;
    }

    /// <summary>Hebt abgelaufene Reservierungen auf und gibt sie zurück.</summary>
    public async Task<List<IpReservation>> CleanupReservationsAsync(CancellationToken ct = default)
    {
        var now = Now;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expired = await db.IpReservations.Where(r => r.ExpiresUtc != null && r.ExpiresUtc <= now).ToListAsync(ct);
        if (expired.Count == 0)
            return expired;
        db.IpReservations.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired;
    }
}
