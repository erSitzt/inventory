using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Ergebnis eines Weckversuchs: abgeschickt heißt nicht aufgewacht – Wake-on-LAN kennt keine Antwort.</summary>
/// <param name="Macs">Die angesprochenen MAC-Adressen; leer, wenn keine bekannt ist.</param>
/// <param name="Packets">Vom Server abgeschickte Pakete.</param>
/// <param name="Relays">Rechner mit Agent im Subnetz des Ziels, die gebeten wurden, das Paket dort zu senden.</param>
public sealed record WakeResult(int ComputerId, string ComputerName, IReadOnlyList<string> Macs, int Packets, IReadOnlyList<string> Relays)
{
    /// <summary>false trotz bekannter MAC-Adresse heißt: der Server hatte keinen Weg ins Netz und kein Agent stand bereit.</summary>
    public bool Sent => Macs.Count > 0 && (Packets > 0 || Relays.Count > 0);

    /// <summary>Abgeschlossen, sobald die Aufträge dieses Aufrufs an die Agenten beantwortet oder verfallen sind. Niemand muss darauf warten.</summary>
    public Task Relayed { get; init; } = Task.CompletedTask;
}

/// <summary>
/// Weckt Rechner per Wake-on-LAN über die MAC-Adressen ihres letzten Scans. Der Server sendet selbst und bittet
/// zusätzlich eingeschaltete Rechner mit Agent im Subnetz des Ziels, das Paket dort zu senden: Broadcasts des Servers
/// kommen über Router hinweg meist nicht an.
/// </summary>
public sealed class WakeService(IDbContextFactory<InventoryDbContext> dbFactory, IWakeSender sender, IEnumerable<IRemoteTransport>? transports = null, TimeProvider? time = null)
{
    /// <summary>So lange braucht ein Rechner nach dem Wecken etwa, bis er im Netz antwortet.</summary>
    public static readonly TimeSpan BootWait = TimeSpan.FromMinutes(2);
    /// <summary>Ein Agent gilt als bereit, wenn er sich in dieser Zeit gemeldet hat; er fragt ununterbrochen nach, vermerkt wird es minütlich.</summary>
    public static readonly TimeSpan RelaySeen = TimeSpan.FromMinutes(3);
    /// <summary>Holt der Agent den Auftrag nicht in dieser Zeit ab (er ist mit einem Job beschäftigt), verfällt er: später wecken wäre eine Überraschung.</summary>
    public static readonly TimeSpan RelayTimeout = TimeSpan.FromMinutes(1);
    /// <summary>Höchstens so viele Agenten je Ziel; der zweite hilft, wenn der erste gerade einen Job ausführt.</summary>
    public const int RelaysPerTarget = 2;

    private readonly IRemoteTransport? _agents = transports?.FirstOrDefault(t => t.Kind == TransportKind.Agent);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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
        var relays = await RelaysAsync(db, ct);

        var results = new List<WakeResult>();
        var orders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var computer in computers)
        {
            var own = adapters[computer.Id].Select(a => new WakeOnLan.Adapter(a.MacAddress, Split(a.IpAddresses), Split(a.SubnetMasks))).ToList();
            var plan = WakeOnLan.PlanFor(computer.WakeMac, computer.IpAddress, own);
            var macs = plan.Macs.Select(WakeOnLan.Format).ToList();
            var packets = 0;
            foreach (var mac in plan.Macs)
                packets += await sender.SendAsync(WakeOnLan.MagicPacket(mac), plan.Addresses, ct);

            // Wo der Rechner zuletzt stand: dort muss das Paket ankommen.
            List<string> lastSeen = [.. own.SelectMany(a => a.Addresses), .. computer.IpAddress is { } ip ? new[] { ip } : []];
            var asked = macs.Count == 0 ? [] : relays.Where(r => r.Id != computer.Id && WakeOnLan.Reaches(r.Adapters, lastSeen)).Take(RelaysPerTarget).Select(r => r.Name).ToList();
            foreach (var relay in asked)
            {
                if (!orders.TryGetValue(relay, out var list))
                    orders[relay] = list = [];
                list.AddRange(macs.Where(m => !list.Contains(m)));
            }
            results.Add(new WakeResult(computer.Id, computer.Name, macs, packets, asked));
        }
        var relayed = Task.WhenAll(orders.Select(o => RelayAsync(o.Key, o.Value)));
        return [.. results.Select(r => r with { Relayed = relayed })];
    }

    private sealed record Relay(int Id, string Name, List<WakeOnLan.Adapter> Adapters);

    /// <summary>Rechner, deren Agent sich gerade gemeldet hat, die zuletzt gemeldeten zuerst.</summary>
    private async Task<List<Relay>> RelaysAsync(InventoryDbContext db, CancellationToken ct)
    {
        if (_agents is null)
            return [];
        var since = _time.GetUtcNow().UtcDateTime - RelaySeen;
        var online = await db.Computers.AsNoTracking()
            .Where(c => c.AgentKeyHash != null && c.AgentLastSeenUtc >= since)
            .OrderByDescending(c => c.AgentLastSeenUtc).Select(c => new { c.Id, c.Name, c.IpAddress }).ToListAsync(ct);
        if (online.Count == 0)
            return [];
        var ids = online.Select(c => c.Id).ToList();
        var adapters = (await db.NetworkAdapters.AsNoTracking().Where(a => ids.Contains(a.ComputerId))
            .Select(a => new { a.ComputerId, a.IpAddresses, a.SubnetMasks }).ToListAsync(ct)).ToLookup(a => a.ComputerId);
        return [.. online.Select(c =>
        {
            List<WakeOnLan.Adapter> known = [.. adapters[c.Id].Select(a => new WakeOnLan.Adapter(null, Split(a.IpAddresses), Split(a.SubnetMasks)))];
            // Ohne IPv4-Adresse aus einem Scan (nie gescannt, nur IPv6 gemeldet) zählt die zuletzt gesehene Adresse.
            if (c.IpAddress is { } ip && !known.Any(a => a.Addresses.Any(x => WakeOnLan.Broadcast(x) is not null)))
                known.Add(new WakeOnLan.Adapter(null, [ip], []));
            return new Relay(c.Id, c.Name, known);
        })];
    }

    /// <summary>
    /// Übergibt dem Agenten die MAC-Adressen und wartet nicht auf das Ergebnis des Aufrufers: der Auftrag läuft im
    /// Hintergrund und verfällt nach <see cref="RelayTimeout"/>. Ein Fehler ist kein Fehler des Weckens – der Server hat selbst gesendet.
    /// </summary>
    private async Task RelayAsync(string host, List<string> macs)
    {
        using var timeout = new CancellationTokenSource(RelayTimeout);
        var manifest = new JobManifest
        {
            Steps = [new JobStep { Name = "Wake-on-LAN", Type = StepType.WakeOnLan, Script = string.Join(",", macs), TimeoutSeconds = 30 }],
        };
        try
        {
            await _agents!.RunAsync(new RemoteTarget(host, null), manifest, [], null, timeout.Token);
        }
        catch (Exception ex) when (ex is RemoteException or OperationCanceledException)
        {
        }
    }

    private static string[] Split(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
