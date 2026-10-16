using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>Knotenarten der Netzwerkkarte.</summary>
public static class MapNodeType
{
    public const string Subnet = "subnet";
    public const string Router = "router";
    public const string Gateway = "gateway";
    public const string Device = "device";
    /// <summary>Sammelknoten für die Rechner eines Subnetzes.</summary>
    public const string Group = "group";
    public const string Computer = "computer";
}

/// <param name="Icon">Art des Geräts (<see cref="DeviceKind"/>) oder Rolle des Rechners (<see cref="ChassisKind"/>); die Oberfläche wählt daraus das Symbol.</param>
/// <param name="Group">Bei Rechnern: der Sammelknoten, in dem sie zusammengefasst sind.</param>
/// <param name="Assumed">Bei Subnetzen: nur angenommen (/24 um eine Adresse), weil keine Maske bekannt ist.</param>
public sealed record MapNode(string Id, string Type, string Label, string? Detail, string? Parent, string? Href, string Icon,
    string? Group = null, bool Online = true, int Count = 0, bool Assumed = false);

public sealed record MapEdge(string Source, string Target);

public sealed record NetworkMap(List<MapNode> Nodes, List<MapEdge> Edges)
{
    public int Subnets => Nodes.Count(n => n.Type == MapNodeType.Subnet);
}

/// <summary>Ein Rechner mit seinen Adressen, wie die Karte ihn braucht.</summary>
/// <param name="Addresses">Je Adresse die Präfixlänge (null = Maske unbekannt).</param>
public sealed record MapComputer(int Id, string Name, bool Online, ChassisKind Role, IReadOnlyList<(string Ip, int? Prefix)> Addresses, IReadOnlyList<string> Gateways);

/// <summary>
/// Leitet aus dem Inventar eine Netzwerkkarte ab: Subnetze aus Adresse und Maske der Rechner, den Schnittstellen der Router und
/// den CIDR-Angaben der Gerätebereiche; Router aus dem Gerätescan und den Gateways der Rechner. Rechner werden je Subnetz zu einem
/// Sammelknoten zusammengefasst, Netzwerkgeräte stehen einzeln.
/// </summary>
public static class NetworkMapBuilder
{
    private const int AssumedPrefix = 24;

    public static NetworkMap Build(IReadOnlyList<MapComputer> computers, IReadOnlyList<NetworkDevice> devices, IReadOnlyList<(string Name, string Addresses)> ranges)
    {
        var subnets = new Dictionary<string, (string? Name, bool Assumed)>();
        void AddSubnet(string? cidr, string? name = null)
        {
            if (cidr is null || IpRanges.PrefixOf(cidr) is < 8 or > 30)
                return;
            if (!subnets.TryGetValue(cidr, out var known) || known.Assumed || (known.Name is null && name is not null))
                subnets[cidr] = (name ?? (subnets.TryGetValue(cidr, out var old) ? old.Name : null), false);
        }

        foreach (var computer in computers)
            foreach (var (ip, prefix) in computer.Addresses.Where(a => a.Prefix is not null && IpRanges.IsUsable(a.Ip)))
                AddSubnet(IpRanges.Network(ip, prefix!.Value));
        foreach (var device in devices)
            foreach (var (ip, prefix) in Interfaces(device))
                AddSubnet(IpRanges.Network(ip, prefix));
        foreach (var (name, addresses) in ranges)
            foreach (var token in addresses.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(t => t.Contains('/')))
                if (token.Split('/') is [var network, var bits] && int.TryParse(bits, out var prefix))
                    AddSubnet(IpRanges.Network(network, prefix), name);

        // Längster Präfix gewinnt; ohne bekanntes Netz ein angenommenes /24.
        string SubnetOf(string ip)
        {
            var match = subnets.Where(s => !s.Value.Assumed && IpRanges.Contains(s.Key, ip)).Select(s => s.Key).OrderByDescending(IpRanges.PrefixOf).FirstOrDefault();
            if (match is not null)
                return match;
            var assumed = IpRanges.Network(ip, AssumedPrefix)!;
            subnets.TryAdd(assumed, (null, true));
            return assumed;
        }

        var nodes = new List<MapNode>();
        var edges = new HashSet<MapEdge>();
        var linkedComputers = computers.Select(c => c.Id).ToHashSet();

        // Router: gescannte Geräte, die routen (oder deren Adresse ein Gateway ist), zusammengeführt über Schnittstellen und MAC.
        var gateways = computers.SelectMany(c => c.Gateways).Where(IpRanges.IsUsable).ToHashSet();
        var routerDevices = devices.Where(d => d.Kind is DeviceKind.Router or DeviceKind.Firewall || Interfaces(d).Any() || gateways.Contains(d.IpAddress)).ToList();
        var routers = new List<(string Id, HashSet<string> Addresses, NetworkDevice Device)>();
        foreach (var device in routerDevices.OrderByDescending(d => Interfaces(d).Count()))
        {
            var addresses = Interfaces(device).Select(i => i.Ip).Append(device.IpAddress).ToHashSet();
            var same = routers.FirstOrDefault(r => r.Addresses.Overlaps(addresses) || SameBox(r.Device, device));
            if (same.Id is not null)
            {
                same.Addresses.UnionWith(addresses);
                continue;
            }
            routers.Add(($"d{device.Id}", addresses, device));
        }
        foreach (var (id, addresses, device) in routers)
        {
            nodes.Add(new MapNode(id, MapNodeType.Router, device.DisplayName, string.Join(", ", addresses.OrderBy(IpRanges.SortKey)), null, $"devices/{device.Id}", device.Kind.ToString()));
            foreach (var (ip, prefix) in Interfaces(device))
                edges.Add(new MapEdge(id, IpRanges.Network(ip, prefix)!));
            edges.Add(new MapEdge(id, SubnetOf(device.IpAddress)));
        }
        // Gateways der Rechner, für die kein Router gefunden wurde.
        foreach (var computer in computers)
        {
            foreach (var gateway in computer.Gateways.Where(IpRanges.IsUsable).Distinct())
            {
                var routerId = routers.FirstOrDefault(r => r.Addresses.Contains(gateway)).Id;
                if (routerId is null)
                {
                    routerId = $"g{gateway}";
                    if (nodes.All(n => n.Id != routerId))
                        nodes.Add(new MapNode(routerId, MapNodeType.Gateway, gateway, null, null, null, nameof(DeviceKind.Router)));
                }
                edges.Add(new MapEdge(routerId, SubnetOf(gateway)));
            }
        }

        // Netzwerkgeräte einzeln; als Rechner inventarisierte Windows-Geräte erscheinen nur als Rechner.
        foreach (var device in devices.Where(d => !routerDevices.Contains(d) && !(d.ComputerId is { } id && linkedComputers.Contains(id))))
        {
            var detail = string.Join(" · ", new[] { device.IpAddress, device.Model ?? device.Manufacturer }.Where(t => !string.IsNullOrEmpty(t)));
            nodes.Add(new MapNode($"d{device.Id}", MapNodeType.Device, device.DisplayName, detail, SubnetOf(device.IpAddress), $"devices/{device.Id}", device.Kind.ToString()));
        }

        // Rechner: je Subnetz ein Sammelknoten, die Rechner selbst als (zunächst ausgeblendete) Mitglieder.
        var members = new Dictionary<string, List<MapNode>>();
        foreach (var computer in computers)
        {
            foreach (var cidr in computer.Addresses.Where(a => IpRanges.IsUsable(a.Ip))
                         .Select(a => a.Prefix is { } p && subnets.ContainsKey(IpRanges.Network(a.Ip, p)!) ? IpRanges.Network(a.Ip, p)! : SubnetOf(a.Ip)).Distinct())
            {
                var group = $"c{cidr}";
                var ip = computer.Addresses.First(a => IpRanges.Contains(cidr, a.Ip)).Ip;
                if (!members.TryGetValue(cidr, out var list))
                    members[cidr] = list = [];
                list.Add(new MapNode($"c{computer.Id}@{cidr}", MapNodeType.Computer, computer.Name, ip, cidr, $"computers/{computer.Id}", computer.Role.ToString(), group, computer.Online));
            }
        }
        foreach (var (cidr, list) in members)
        {
            nodes.Add(new MapNode($"c{cidr}", MapNodeType.Group, cidr, null, cidr, null, nameof(ChassisKind.Desktop), Count: list.Count, Online: list.Any(m => m.Online)));
            nodes.AddRange(list.OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase));
        }

        // Subnetze zuletzt: erst jetzt stehen auch die angenommenen fest. Leere Netze (nur aus einem Bereich) entfallen.
        var used = nodes.Select(n => n.Parent).Concat(edges.Select(e => e.Target)).ToHashSet();
        nodes.InsertRange(0, subnets.Where(s => used.Contains(s.Key)).OrderBy(s => IpRanges.SortKey(s.Key.Split('/')[0]))
            .Select(s => new MapNode(s.Key, MapNodeType.Subnet, s.Key, s.Value.Name, null, null, "subnet", Assumed: s.Value.Assumed)));
        return new NetworkMap(nodes, [.. edges.Where(e => nodes.Any(n => n.Id == e.Target))]);
    }

    /// <summary>
    /// Dasselbe Gerät unter verschiedenen Adressen – typisch für die zentrale Firewall, die in jedem Netz das Gateway ist:
    /// gleiche MAC-Adresse, gleiche Seriennummer oder gleicher SNMP-Name.
    /// </summary>
    private static bool SameBox(NetworkDevice a, NetworkDevice b) =>
        (a.MacAddress is not null && a.MacAddress == b.MacAddress)
        || (!string.IsNullOrWhiteSpace(a.SerialNumber) && string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.OrdinalIgnoreCase))
        || (a.SnmpResponded && b.SnmpResponded && !string.IsNullOrWhiteSpace(a.DiscoveredName) && string.Equals(a.DiscoveredName, b.DiscoveredName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Ip, int Prefix)> Interfaces(NetworkDevice device) =>
        (device.InterfaceAddresses ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(i => i.Split('/'))
            .Where(p => p.Length == 2 && IpRanges.IsUsable(p[0]) && int.TryParse(p[1], out _))
            .Select(p => (p[0], int.Parse(p[1])));
}
