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
    /// <summary>Der Inventory-Server selbst; seine Wege (Traceroute) zeigen, wo er im Netz steht.</summary>
    public const string Server = "server";
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

    public static NetworkMap Build(IReadOnlyList<MapComputer> computers, IReadOnlyList<NetworkDevice> devices, IReadOnlyList<(string Name, string Addresses)> ranges,
        IReadOnlyList<NetworkTrace>? traces = null)
    {
        traces ??= [];
        var ranged = new HashSet<string>();
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
                if (token.Split('/') is [var network, var bits] && int.TryParse(bits, out var prefix) && IpRanges.Network(network, prefix) is { } cidr)
                {
                    AddSubnet(cidr, name);
                    ranged.Add(cidr);
                }

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

        // Router: Adressen, die zum selben Gerät gehören, werden zusammengefasst (Union-Find über die Adressen). Belege: die
        // Schnittstellen eines Routers (SNMP), dasselbe Gerät unter mehreren Adressen (MAC, Seriennummer, SNMP-Name) und der
        // Traceroute: der letzte Router vor einem Ziel ist das Gateway des Zielnetzes.
        var gateways = computers.SelectMany(c => c.Gateways).Where(IpRanges.IsUsable).ToHashSet();
        var hopAddresses = traces.SelectMany(t => t.HopList).OfType<string>().Where(IpRanges.IsUsable).ToHashSet();
        var routerDevices = devices.Where(d => d.Kind is DeviceKind.Router or DeviceKind.Firewall || Interfaces(d).Any()
            || gateways.Contains(d.IpAddress) || hopAddresses.Contains(d.IpAddress)).ToList();
        var root = new Dictionary<string, string>();
        string Find(string ip)
        {
            if (!root.TryGetValue(ip, out var parent))
                return root[ip] = ip;
            return parent == ip ? ip : root[ip] = Find(parent);
        }
        void Union(string a, string b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
                root[ra] = rb;
        }

        foreach (var device in routerDevices)
            foreach (var (ip, _) in Interfaces(device))
                Union(ip, device.IpAddress);
        for (var i = 0; i < routerDevices.Count; i++)
            for (var j = i + 1; j < routerDevices.Count; j++)
                if (SameBox(routerDevices[i], routerDevices[j]))
                    Union(routerDevices[i].IpAddress, routerDevices[j].IpAddress);
        foreach (var gateway in gateways)
            Find(gateway);
        foreach (var hop in hopAddresses)
            Find(hop);
        foreach (var trace in traces.Where(t => t.Reached && IpRanges.IsUsable(t.Target)))
        {
            var last = trace.HopList.LastOrDefault();
            var subnet = SubnetOf(trace.Target);
            if (last is null || IpRanges.Contains(subnet, last))
                continue;
            // Das Gateway des Zielnetzes: von den Rechnern gemeldet oder eine Router-Schnittstelle in diesem Netz.
            var candidates = gateways.Where(g => IpRanges.Contains(subnet, g))
                .Concat(routerDevices.SelectMany(d => Interfaces(d).Select(i => i.Ip).Append(d.IpAddress)).Where(ip => IpRanges.Contains(subnet, ip)))
                .Select(Find).Distinct().ToList();
            if (candidates.Count == 1)
                Union(last, candidates[0]);
        }

        // Je Gruppe ein Knoten: das gescannte Gerät mit den meisten Schnittstellen, sonst ein Gateway oder Hop ohne Gerät.
        var routerOf = new Dictionary<string, string>();
        foreach (var group in root.Keys.ToList().GroupBy(Find))
        {
            var addresses = group.OrderBy(IpRanges.SortKey).ToList();
            var device = routerDevices.Where(d => group.Contains(d.IpAddress)).OrderByDescending(d => Interfaces(d).Count()).FirstOrDefault();
            var id = device is null ? $"g{addresses.FirstOrDefault(gateways.Contains) ?? addresses[0]}" : $"d{device.Id}";
            foreach (var ip in addresses)
                routerOf[ip] = id;
            if (device is not null)
                nodes.Add(new MapNode(id, MapNodeType.Router, device.DisplayName, string.Join(", ", addresses), null, $"devices/{device.Id}", device.Kind.ToString()));
            else
                nodes.Add(new MapNode(id, MapNodeType.Gateway, addresses.FirstOrDefault(gateways.Contains) ?? addresses[0], string.Join(", ", addresses), null, null, nameof(DeviceKind.Router)));

            // Kanten in die Netze, in denen der Router eine Adresse hat – nicht für reine Hop-Adressen (sonst entstünden Transfernetze als /24).
            foreach (var d in routerDevices.Where(d => group.Contains(d.IpAddress)))
            {
                foreach (var (ip, prefix) in Interfaces(d))
                    edges.Add(new MapEdge(id, IpRanges.Network(ip, prefix)!));
                edges.Add(new MapEdge(id, SubnetOf(d.IpAddress)));
            }
            foreach (var gateway in addresses.Where(gateways.Contains))
                edges.Add(new MapEdge(id, SubnetOf(gateway)));
        }

        // Wege des Servers: Router hintereinander, der Server am Anfang.
        if (traces.Count > 0)
        {
            nodes.Add(new MapNode("server", MapNodeType.Server, "Inventory", null, null, null, nameof(ChassisKind.Server)));
            foreach (var trace in traces)
            {
                var chain = trace.HopList.OfType<string>().Where(routerOf.ContainsKey).Select(h => routerOf[h]).ToList();
                if (routerOf.TryGetValue(trace.Target, out var targetRouter))
                    chain.Add(targetRouter);
                chain = [.. chain.Where((id, i) => i == 0 || chain[i - 1] != id)];
                if (chain.Count == 0)
                {
                    // Ohne Router dazwischen steht der Server im selben Netz wie das Ziel.
                    if (trace.Reached && trace.HopList.Count == 0)
                        edges.Add(new MapEdge("server", SubnetOf(trace.Target)));
                    continue;
                }
                edges.Add(new MapEdge("server", chain[0]));
                for (var i = 1; i < chain.Count; i++)
                    edges.Add(new MapEdge(chain[i - 1], chain[i]));
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
        // Transfernetze (/29 und kleiner) ohne Mitglieder zeigt die Karte als Verbindung zwischen Routern, nicht als eigenes Netz.
        var parents = nodes.Select(n => n.Parent).ToHashSet();
        var used = parents.Concat(edges.Select(e => e.Target)).ToHashSet();
        nodes.InsertRange(0, subnets.Where(s => used.Contains(s.Key) && (IpRanges.PrefixOf(s.Key) < 29 || parents.Contains(s.Key) || ranged.Contains(s.Key))).OrderBy(s => IpRanges.SortKey(s.Key.Split('/')[0]))
            .Select(s => new MapNode(s.Key, MapNodeType.Subnet, s.Key, s.Value.Name, null, null, "subnet", Assumed: s.Value.Assumed)));
        var ids = nodes.Select(n => n.Id).ToHashSet();
        return new NetworkMap(nodes, [.. edges.Where(e => e.Source != e.Target && ids.Contains(e.Source) && ids.Contains(e.Target))]);
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
