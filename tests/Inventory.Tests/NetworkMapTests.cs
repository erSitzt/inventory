using System.Net;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Devices;

namespace Inventory.Tests;

public class NetworkMapTests
{
    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.252.0", 22)]
    [InlineData("255.255.255.255", 32)]
    [InlineData("0.0.0.0", 0)]
    [InlineData("255.0.255.0", null)]
    [InlineData("ffff::", null)]
    [InlineData(null, null)]
    public void Masks_become_prefix_lengths(string? mask, int? prefix) => Assert.Equal(prefix, IpRanges.PrefixLength(mask));

    [Fact]
    public void Networks_and_containment()
    {
        Assert.Equal("10.0.4.0/22", IpRanges.Network("10.0.5.17", 22));
        Assert.Equal("10.0.0.7/32", IpRanges.Network("10.0.0.7", 32));
        Assert.Null(IpRanges.Network("not an ip", 24));
        Assert.True(IpRanges.Contains("10.0.4.0/22", "10.0.7.255"));
        Assert.False(IpRanges.Contains("10.0.4.0/22", "10.0.8.1"));
        Assert.False(IpRanges.IsUsable("169.254.10.1"));
        Assert.False(IpRanges.IsUsable("127.0.0.1"));
        Assert.True(IpRanges.IsUsable("192.168.1.10"));
    }

    private static MapComputer Pc(int id, string name, string ip, int? prefix = 24, string? gateway = null) =>
        new(id, name, true, ChassisKind.Desktop, [(ip, prefix)], gateway is null ? [] : [gateway]);

    private static NetworkDevice Device(int id, string ip, DeviceKind kind, string? interfaces = null, string? mac = null, int? computerId = null) =>
        new() { Id = id, IpAddress = ip, DetectedKind = kind, InterfaceAddresses = interfaces, MacAddress = mac, ComputerId = computerId, DiscoveredName = $"dev-{id}" };

    [Fact]
    public void Computers_are_grouped_per_subnet_and_devices_stand_alone()
    {
        var map = NetworkMapBuilder.Build(
            [Pc(1, "PC-1", "10.0.0.10", gateway: "10.0.0.1"), Pc(2, "PC-2", "10.0.0.11", gateway: "10.0.0.1"), Pc(3, "PC-3", "10.20.0.5", gateway: "10.20.0.1")],
            [Device(10, "10.0.0.50", DeviceKind.Printer), Device(11, "10.0.0.101", DeviceKind.Windows, computerId: 1)],
            []);

        Assert.Equal(["10.0.0.0/24", "10.20.0.0/24"], map.Nodes.Where(n => n.Type == MapNodeType.Subnet).Select(n => n.Id));
        var group = map.Nodes.Single(n => n.Id == "c10.0.0.0/24");
        Assert.Equal(2, group.Count);
        Assert.All(map.Nodes.Where(n => n.Type == MapNodeType.Computer && n.Parent == "10.0.0.0/24"), n => Assert.Equal(group.Id, n.Group));
        Assert.Equal("10.0.0.0/24", map.Nodes.Single(n => n.Id == "d10").Parent);
        // Der als Rechner inventarisierte Windows-Fund erscheint nur einmal.
        Assert.DoesNotContain(map.Nodes, n => n.Id == "d11");
        // Gateways ohne gescanntes Gerät werden zu eigenen Knoten.
        Assert.Contains(new MapEdge("g10.0.0.1", "10.0.0.0/24"), map.Edges);
        Assert.Contains(new MapEdge("g10.20.0.1", "10.20.0.0/24"), map.Edges);
    }

    [Fact]
    public void A_router_with_several_interfaces_connects_its_subnets_as_one_node()
    {
        var map = NetworkMapBuilder.Build(
            [Pc(1, "PC-1", "10.0.0.10", gateway: "10.0.0.1"), Pc(2, "PC-2", "10.20.0.5", gateway: "10.20.0.1")],
            [Device(1, "10.0.0.1", DeviceKind.Firewall, "10.0.0.1/24,10.20.0.1/24,10.0.1.1/24")],
            []);

        var router = Assert.Single(map.Nodes, n => n.Type is MapNodeType.Router or MapNodeType.Gateway);
        Assert.Equal("d1", router.Id);
        Assert.Equal(["10.0.0.0/24", "10.0.1.0/24", "10.20.0.0/24"], map.Edges.Where(e => e.Source == "d1").Select(e => e.Target).Order());
        // Das Server-Netz ist nur über die Schnittstelle bekannt und erscheint trotzdem.
        Assert.Contains(map.Nodes, n => n.Id == "10.0.1.0/24");
    }

    [Fact]
    public void Gateways_with_the_same_mac_are_one_router()
    {
        var map = NetworkMapBuilder.Build(
            [Pc(1, "PC-1", "10.0.0.10", gateway: "10.0.0.1"), Pc(2, "PC-2", "10.20.0.5", gateway: "10.20.0.1")],
            [Device(1, "10.0.0.1", DeviceKind.Router, mac: "00:11:22:33:44:55"), Device(2, "10.20.0.1", DeviceKind.Router, mac: "00:11:22:33:44:55")],
            []);
        var router = Assert.Single(map.Nodes, n => n.Type is MapNodeType.Router or MapNodeType.Gateway);
        Assert.Equal(2, map.Edges.Count(e => e.Source == router.Id));
    }

    [Fact]
    public void Gateways_with_the_same_serial_number_are_one_router()
    {
        var first = Device(1, "10.0.0.1", DeviceKind.Firewall);
        var second = Device(2, "10.20.0.1", DeviceKind.Firewall);
        first.SerialNumber = second.SerialNumber = "FGT60F0001";
        var map = NetworkMapBuilder.Build(
            [Pc(1, "PC-1", "10.0.0.10", gateway: "10.0.0.1"), Pc(2, "PC-2", "10.20.0.5", gateway: "10.20.0.1")], [first, second], []);
        Assert.Single(map.Nodes, n => n.Type is MapNodeType.Router or MapNodeType.Gateway);
    }

    [Fact]
    public void Unknown_masks_assume_a_slash_24_and_the_longest_prefix_wins()
    {
        var map = NetworkMapBuilder.Build(
            [Pc(1, "PC-1", "10.0.4.20", prefix: 22), Pc(2, "PC-2", "172.16.5.9", prefix: null)],
            [Device(1, "10.0.6.9", DeviceKind.Switch), Device(2, "192.168.7.3", DeviceKind.Printer)],
            [("Branch", "192.168.7.0/24")]);

        Assert.Equal("10.0.4.0/22", map.Nodes.Single(n => n.Id == "d1").Parent);
        var assumed = map.Nodes.Single(n => n.Id == "172.16.5.0/24");
        Assert.True(assumed.Assumed);
        var branch = map.Nodes.Single(n => n.Id == "192.168.7.0/24");
        Assert.False(branch.Assumed);
        Assert.Equal("Branch", branch.Detail);
    }

    [Fact]
    public async Task Service_reads_adapters_and_respects_the_operator_scope()
    {
        using var db = new TestDb();
        var computers = await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01 pc-02");
        await using (var context = db.CreateDbContext())
        {
            context.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computers[0].Id, Name = "LAN", IpAddresses = "10.0.0.10, fe80::1", SubnetMasks = "255.255.255.0", Gateways = "10.0.0.1" });
            context.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computers[1].Id, Name = "LAN", IpAddresses = "10.20.0.10", SubnetMasks = "255.255.255.0", Gateways = "10.20.0.1" });
            await context.SaveChangesAsync();
        }
        var service = new NetworkMapService(db);

        var all = await service.BuildAsync();
        Assert.Equal(2, all.Subnets);
        var limited = await service.BuildAsync(new HashSet<int> { computers[0].Id });
        Assert.Equal(["10.0.0.0/24"], limited.Nodes.Where(n => n.Type == MapNodeType.Subnet).Select(n => n.Id));
        Assert.DoesNotContain(limited.Nodes, n => n.Href == $"computers/{computers[1].Id}");
    }

    [Fact]
    public async Task Routers_report_their_interfaces_over_snmp()
    {
        using var agent = new FakeSnmpAgent("public", new()
        {
            ["1.3.6.1.2.1.1.1.0"] = (SnmpValue.OctetString, "Router"u8.ToArray()),
            ["1.3.6.1.2.1.1.2.0"] = (SnmpValue.ObjectId, Ber.EncodeOid("1.3.6.1.4.1.9.1.1")),
            ["1.3.6.1.2.1.4.1.0"] = (SnmpValue.Integer, Ber.EncodeInteger(1)),
            ["1.3.6.1.2.1.4.20.1.1.10.0.0.1"] = (SnmpValue.IpAddress, [10, 0, 0, 1]),
            ["1.3.6.1.2.1.4.20.1.1.10.20.0.1"] = (SnmpValue.IpAddress, [10, 20, 0, 1]),
            ["1.3.6.1.2.1.4.20.1.1.127.0.0.1"] = (SnmpValue.IpAddress, [127, 0, 0, 1]),
            ["1.3.6.1.2.1.4.20.1.3.10.0.0.1"] = (SnmpValue.IpAddress, [255, 255, 255, 0]),
            ["1.3.6.1.2.1.4.20.1.3.10.20.0.1"] = (SnmpValue.IpAddress, [255, 255, 0, 0]),
            ["1.3.6.1.2.1.4.20.1.3.127.0.0.1"] = (SnmpValue.IpAddress, [255, 0, 0, 0]),
        });
        var facts = await SnmpCollector.CollectAsync(IPAddress.Loopback, ["public"], CancellationToken.None, agent.Port);
        Assert.NotNull(facts);
        Assert.Equal(["10.0.0.1/24", "10.20.0.1/16"], facts.InterfaceAddresses);
    }
}
