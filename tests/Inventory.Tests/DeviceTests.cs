using System.Net;
using System.Net.Sockets;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Devices;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public class DeviceTests
{
    [Theory]
    [InlineData("10.0.0.0/24", 254, "10.0.0.1", "10.0.0.254")]
    [InlineData("10.0.0.8/31", 2, "10.0.0.8", "10.0.0.9")]
    [InlineData("10.0.0.5/32", 1, "10.0.0.5", "10.0.0.5")]
    [InlineData("10.0.0.10-20", 11, "10.0.0.10", "10.0.0.20")]
    [InlineData("10.0.0.250-10.0.1.5", 12, "10.0.0.250", "10.0.1.5")]
    [InlineData("10.0.0.7, 10.0.0.3\n10.0.0.7;10.0.0.1-3", 4, "10.0.0.1", "10.0.0.7")]
    public void Ranges_expand_in_order_without_duplicates(string text, int count, string first, string last)
    {
        var addresses = IpRanges.Expand(text);
        Assert.Equal(count, addresses.Count);
        Assert.Equal(first, addresses[0].ToString());
        Assert.Equal(last, addresses[^1].ToString());
    }

    [Theory]
    [InlineData("10.0.0.0/16")]
    [InlineData("10.0.0")]
    [InlineData("10.0.0.20-10")]
    [InlineData("fe80::1")]
    [InlineData("printer.corp.example")]
    public void Invalid_ranges_are_rejected(string text) => Assert.NotNull(IpRanges.Validate(text));

    [Fact]
    public void Ranges_are_capped()
    {
        Assert.Throws<ArgumentException>(() => IpRanges.Expand("10.0.0.0/20, 10.1.0.0/24"));
        // Überlappungen zählen nur einmal.
        Assert.Equal(4094, IpRanges.Expand(string.Join(",", Enumerable.Repeat("10.0.0.0/20", 3)) + ", 10.0.0.0/24").Count);
    }

    [Fact]
    public void Object_identifiers_round_trip()
    {
        Assert.Equal(new byte[] { 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x15 }, Ber.EncodeOid("1.3.6.1.4.1.311.21"));
        Assert.Equal("1.3.6.1.4.1.41112.1.6.3.3", Ber.ReadOid(Ber.EncodeOid("1.3.6.1.4.1.41112.1.6.3.3")));
        Assert.Equal("1.3.6.1.2.1.4.22.1.2.4.192.168.0.1", Ber.ReadOid(Ber.EncodeOid(".1.3.6.1.2.1.4.22.1.2.4.192.168.0.1")));
        // Erster Teilbezeichner über 127 (2.100 = 180) braucht zwei Bytes.
        Assert.Equal(new byte[] { 0x81, 0x34, 0x03 }, Ber.EncodeOid("2.100.3"));
        Assert.Equal("2.100.3", Ber.ReadOid(Ber.EncodeOid("2.100.3")));
        Assert.Throws<FormatException>(() => Ber.EncodeOid("1.40"));
    }

    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x00, 0x80 })]
    [InlineData(-1, new byte[] { 0xFF })]
    [InlineData(-129, new byte[] { 0xFF, 0x7F })]
    [InlineData(70000, new byte[] { 0x01, 0x11, 0x70 })]
    public void Integers_are_encoded_minimally(long value, byte[] expected)
    {
        Assert.Equal(expected, Ber.EncodeInteger(value));
        Assert.Equal(value, Ber.ReadSigned(expected));
    }

    [Fact]
    public async Task Client_gets_and_walks_against_an_agent()
    {
        using var agent = new FakeSnmpAgent("secret", new()
        {
            ["1.3.6.1.2.1.1.1.0"] = (SnmpValue.OctetString, "Brother NC-8700h"u8.ToArray()),
            ["1.3.6.1.2.1.1.2.0"] = (SnmpValue.ObjectId, Ber.EncodeOid("1.3.6.1.4.1.2435.2.3.9.1")),
            ["1.3.6.1.2.1.1.3.0"] = (SnmpValue.TimeTicks, Ber.EncodeInteger(123_400)),
            ["1.3.6.1.2.1.43.11.1.1.6.1.1"] = (SnmpValue.OctetString, "Black Toner"u8.ToArray()),
            ["1.3.6.1.2.1.43.11.1.1.6.1.2"] = (SnmpValue.OctetString, "Drum Unit"u8.ToArray()),
            ["1.3.6.1.2.1.43.11.1.1.8.1.1"] = (SnmpValue.Integer, Ber.EncodeInteger(100)),
        });

        var wrong = new SnmpClient(IPAddress.Loopback, "public", SnmpClient.V2c, TimeSpan.FromMilliseconds(200), agent.Port);
        Assert.Null(await wrong.GetAsync(["1.3.6.1.2.1.1.1.0"], CancellationToken.None));

        var client = new SnmpClient(IPAddress.Loopback, "secret", SnmpClient.V2c, TimeSpan.FromSeconds(2), agent.Port);
        var values = await client.GetAsync(["1.3.6.1.2.1.1.1.0", "1.3.6.1.2.1.1.2.0", "1.3.6.1.2.1.1.3.0", "1.3.6.1.2.1.1.5.0"], CancellationToken.None);
        Assert.NotNull(values);
        Assert.Equal("Brother NC-8700h", values["1.3.6.1.2.1.1.1.0"].AsText());
        Assert.Equal("1.3.6.1.4.1.2435.2.3.9.1", values["1.3.6.1.2.1.1.2.0"].AsText());
        Assert.Equal(123_400, values["1.3.6.1.2.1.1.3.0"].AsNumber());
        Assert.False(values.ContainsKey("1.3.6.1.2.1.1.5.0"));

        var walked = await client.WalkAsync("1.3.6.1.2.1.43.11.1.1.6.1", 10, CancellationToken.None);
        Assert.Equal(["Black Toner", "Drum Unit"], walked.Select(v => v.AsText()));
    }

    [Fact]
    public void Text_values_keep_umlauts_and_show_binary_as_hex()
    {
        Assert.Equal("Büro 2", new SnmpValue("x", SnmpValue.OctetString, "Büro 2\0"u8.ToArray()).AsText());
        Assert.Equal("00:1A:2B:3C:4D:5E", new SnmpValue("x", SnmpValue.OctetString, [0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E]).AsText());
    }

    public static TheoryData<string, DeviceObservation, string?, DeviceKind> Observations => new()
    {
        { "printer MIB", new() { IpAddress = "1", OpenPorts = [80], Snmp = new() { IsPrinter = true, SysObjectId = "1.3.6.1.4.1.11.2.3.9.1" } }, null, DeviceKind.Printer },
        { "switch by bridge MIB", new() { IpAddress = "1", Snmp = new() { SysDescr = "ProVision ROM", SysObjectId = "1.3.6.1.4.1.11.2.3.7.11.181", BridgePorts = 28, Forwarding = false } }, null, DeviceKind.Switch },
        { "router by forwarding", new() { IpAddress = "1", Snmp = new() { SysDescr = "Cisco IOS Software, C1100 Software, Version 17.3.4", SysObjectId = "1.3.6.1.4.1.9.1.2", Forwarding = true } }, null, DeviceKind.Router },
        { "firewall by sysDescr", new() { IpAddress = "1", Snmp = new() { SysDescr = "FortiGate-60F v7.2.8", SysObjectId = "1.3.6.1.4.1.12356.101.1.6005", Forwarding = true } }, null, DeviceKind.Firewall },
        { "NAS on net-snmp", new() { IpAddress = "1", WindowsName = "NAS", Snmp = new() { SysDescr = "Linux DiskStation 4.4.302+", SysObjectId = "1.3.6.1.4.1.8072.3.2.10" } }, null, DeviceKind.Storage },
        { "Linux by SSH", new() { IpAddress = "1", OpenPorts = [22], SshBanner = "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.10" }, null, DeviceKind.Linux },
        { "Samba is not Windows", new() { IpAddress = "1", OpenPorts = [22, 445], WindowsName = "FILES", SshBanner = "SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3" }, null, DeviceKind.Linux },
        { "Windows by SMB", new() { IpAddress = "1", OpenPorts = [135, 445], WindowsName = "PC-1" }, null, DeviceKind.Windows },
        { "raw printing port", new() { IpAddress = "1", OpenPorts = [80, 9100] }, null, DeviceKind.Printer },
        { "printer by MAC", new() { IpAddress = "1", OpenPorts = [80] }, "Brother Industries, LTD.", DeviceKind.Printer },
        { "camera by realm", new() { IpAddress = "1", OpenPorts = [80, 554], HttpRealm = "IP Camera" }, null, DeviceKind.Camera },
        { "ESXi by title", new() { IpAddress = "1", OpenPorts = [443, 902], HttpTitle = "VMware ESXi" }, null, DeviceKind.Hypervisor },
        { "server management", new() { IpAddress = "1", OpenPorts = [443], HttpTitle = "iLO 5" }, null, DeviceKind.Management },
        { "Apple by MAC", new() { IpAddress = "1", OpenPorts = [] }, "Apple, Inc.", DeviceKind.Apple },
        { "nothing to go by", new() { IpAddress = "1", PingReply = true }, null, DeviceKind.Unknown },
    };

    [Theory]
    [MemberData(nameof(Observations))]
    public void Devices_are_classified(string description, DeviceObservation observation, string? macVendor, DeviceKind expected)
    {
        var result = DeviceClassifier.Classify(observation, macVendor);
        Assert.True(expected == result.Kind, $"{description}: {result.Kind} ({result.Reason})");
    }

    [Fact]
    public void Operating_system_and_vendor_are_guessed()
    {
        Assert.Equal("Ubuntu", DeviceClassifier.GuessOs(new() { IpAddress = "1", SshBanner = "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.10" }));
        Assert.Equal("Linux 5.15.0-112-generic", DeviceClassifier.GuessOs(new() { IpAddress = "1", Snmp = new() { SysDescr = "Linux web-01 5.15.0-112-generic #122-Ubuntu SMP" } }));
        Assert.Equal("Windows (Build 17763)", DeviceClassifier.GuessOs(new() { IpAddress = "1", Snmp = new() { SysDescr = "Hardware: Intel64 Family 6 - Software: Windows Version 6.3 (Build 17763 Multiprocessor Free)" } }));
        Assert.Equal(41112, DeviceClassifier.Enterprise("1.3.6.1.4.1.41112.1.6"));
        Assert.Null(DeviceClassifier.Enterprise("1.3.6.1.2.1.1"));
        Assert.Equal("Brother Industries", DeviceClassifier.ShortVendor("Brother Industries, LTD."));
        Assert.Equal("Hangzhou Hikvision Digital", DeviceClassifier.ShortVendor("Hangzhou Hikvision Digital Technology Co.,Ltd."));
    }

    [Fact]
    public void Mac_vendors_come_from_the_registry()
    {
        Assert.Contains("Cisco", MacVendors.Lookup("00-00-0C-12-34-56"));
        // Lokal verwaltete (zufällige) Adressen haben keinen Hersteller.
        Assert.Null(MacVendors.Lookup("02:00:0C:12:34:56"));
        Assert.Null(MacVendors.Lookup("not a mac"));
    }

    [Fact]
    public async Task Ranges_are_validated_and_communities_stored_encrypted()
    {
        using var db = new TestDb();
        var service = new DeviceService(db, new SimulatedDeviceProbe(), new ReversingProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveRangeAsync(null, "Office", "10.0.0.0/8", true, null, null));
        var range = await service.SaveRangeAsync(null, "Office", "10.0.0.0/24", true, "private1\nprivate1\n", null);
        Assert.Equal("1etavirp", range.ProtectedCommunities);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveRangeAsync(null, "office", "10.0.1.0/24", true, null, null));

        // null lässt die Communities unverändert, leer setzt sie zurück.
        range = await service.SaveRangeAsync(range.Id, "Office", "10.0.0.0/24", true, null, null);
        Assert.NotNull(range.ProtectedCommunities);
        range = await service.SaveRangeAsync(range.Id, "Office", "10.0.0.0/24", true, "", null);
        Assert.Null(range.ProtectedCommunities);
    }

    [Fact]
    public async Task Scan_uses_the_communities_of_the_range_or_the_general_ones()
    {
        using var db = new TestDb();
        var probe = new RecordingProbe();
        var service = new DeviceService(db, probe, new ReversingProtector());
        await service.SaveRangeAsync(null, "A", "10.0.0.1", true, null, null);
        await service.SaveRangeAsync(null, "B", "10.0.1.1", true, "own", null);
        await service.SaveRangeAsync(null, "C", "10.0.2.1", false, null, null);

        await service.ScanAsync();
        Assert.Equal(["public"], probe.Seen["10.0.0.1"]);
        Assert.Equal(["own"], probe.Seen["10.0.1.1"]);
        Assert.False(probe.Seen.ContainsKey("10.0.2.1"));

        // Ohne Bereich: nur die Gateways der Rechner, die in keinem Bereich liegen.
        var computers = await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01");
        await using (var context = db.CreateDbContext())
        {
            context.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computers[0].Id, Name = "LAN", Gateways = "10.0.0.1, 10.9.0.1" });
            await context.SaveChangesAsync();
        }
        probe.Seen.Clear();
        Assert.Contains("1 of 1", await service.ScanAsync());
        Assert.True(probe.Seen.ContainsKey("10.9.0.1"));
        Assert.Equal(1, probe.Seen.Keys.Count(k => k == "10.0.0.1"));
        Assert.DoesNotContain("10.9.0.1", (await service.ScanAsync(1)) ?? "");

        await service.SetDefaultCommunitiesAsync("one\ntwo");
        await service.ScanAsync();
        Assert.Equal(["one", "two"], probe.Seen["10.0.0.1"]);
    }

    [Fact]
    public async Task Scan_stores_devices_links_computers_and_keeps_known_devices()
    {
        using var db = new TestDb();
        await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("PC-101.corp.example");
        var service = new DeviceService(db, new SimulatedDeviceProbe(), new ReversingProtector());
        var range = await service.SaveRangeAsync(null, "Office", "10.0.0.1-12, 10.0.0.101, 10.0.0.150, 10.0.0.200", true, null, null);

        var result = await service.ScanAsync(range.Id);
        Assert.Contains("8 of 15", result);
        var devices = await service.ListAsync();
        Assert.Equal(8, devices.Count);

        var printer = devices.Single(d => d.IpAddress == "10.0.0.12");
        Assert.Equal(DeviceKind.Printer, printer.Kind);
        Assert.Equal("TASKalfa 2553ci", printer.Model);
        Assert.Equal(5, printer.Supplies.Count);
        Assert.Equal(9, printer.Supplies.Single(s => s.Description == "Cyan Toner").Percent);
        Assert.Contains("Kyocera", printer.MacVendor, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("PC-101.corp.example", devices.Single(d => d.IpAddress == "10.0.0.101").Computer?.Name);
        var guest = devices.Single(d => d.IpAddress == "10.0.0.150");
        Assert.Equal(DeviceKind.Windows, guest.Kind);
        Assert.Null(guest.ComputerId);
        Assert.Equal(DeviceKind.Firewall, devices.Single(d => d.IpAddress == "10.0.0.1").Kind);
        Assert.Equal(DeviceKind.Switch, devices.Single(d => d.IpAddress == "10.0.0.2").Kind);

        // Ein zweiter Scan legt nichts doppelt an; Name und Art von Hand bleiben.
        await service.UpdateAsync(printer.Id, "Copier floor 2", DeviceKind.Printer, "leased");
        Assert.Contains(", 0 new", await service.ScanAsync(range.Id));
        var again = await service.GetAsync(printer.Id);
        Assert.Equal("Copier floor 2", again!.DisplayName);
        Assert.Equal(5, again.Supplies.Count);
        Assert.Equal(8, (await service.ListAsync()).Count);
    }

    [Fact]
    public async Task Windows_devices_are_added_to_the_computer_list_or_linked_to_a_listed_one()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var service = new DeviceService(db, new SimulatedDeviceProbe(), new ReversingProtector());
        var range = await service.SaveRangeAsync(null, "Office", "10.0.0.30-32, 10.0.0.101, 10.0.0.150", true, null, null);
        await service.ScanAsync(range.Id);
        var devices = await service.ListAsync();
        Assert.Equal("SRV-FILE01", devices.Single(d => d.IpAddress == "10.0.0.32").WindowsName);
        Assert.Equal([DeviceKind.Linux, DeviceKind.Linux], devices.Where(d => d.IpAddress is "10.0.0.30" or "10.0.0.31").Select(d => d.Kind));
        Assert.Equal(["10.0.0.32", "10.0.0.101", "10.0.0.150"], devices.Where(d => d.CanAdopt).Select(d => d.IpAddress));

        // Inzwischen von Hand eingetragen: das Gerät wird nur verknüpft. Linux-Geräte werden übergangen.
        await computers.ImportAsync("pc-101");
        var result = await service.AdoptAsync([.. devices.Select(d => d.Id)]);
        Assert.Equal(["SRV-FILE01", "LAPTOP-GUEST7"], result.AddedNames);
        Assert.Equal(["10.0.0.101 → pc-101"], result.Linked);
        Assert.Equal("SRV-FILE01, LAPTOP-GUEST7, 10.0.0.101 → pc-101", result.Details);

        var list = await computers.ListAsync();
        Assert.Equal(["LAPTOP-GUEST7", "pc-101", "SRV-FILE01"], list.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase));
        var server = list.Single(c => c.Name == "SRV-FILE01");
        Assert.Equal(("10.0.0.32", "srv-file01.corp.example"), (server.IpAddress, server.DnsName));
        devices = await service.ListAsync();
        Assert.Equal(server.Id, devices.Single(d => d.IpAddress == "10.0.0.32").ComputerId);
        Assert.Empty(devices.Where(d => d.CanAdopt));

        // Ein zweiter Aufruf ändert nichts.
        Assert.Empty((await service.AdoptAsync([.. devices.Select(d => d.Id)])).AddedIds);
    }

    [Fact]
    public void WinRm_over_https_alone_means_windows() =>
        Assert.Equal(DeviceKind.Windows, DeviceClassifier.Classify(new DeviceObservation { IpAddress = "10.0.0.5", OpenPorts = [5986] }, null).Kind);

    [Fact]
    public async Task A_device_with_a_known_mac_moves_to_its_new_address()
    {
        using var db = new TestDb();
        var service = new DeviceService(db, new SimulatedDeviceProbe(), new ReversingProtector());
        var range = await service.SaveRangeAsync(null, "Office", "10.0.0.0/24", true, null, null);
        Assert.Equal(1, await service.StoreAsync(range.Id, [new DeviceObservation { IpAddress = "10.0.0.50", PingReply = true, MacAddress = "30-05-5c-12-34-56", OpenPorts = [9100] }], default));
        var first = Assert.Single(await service.ListAsync());
        await service.UpdateAsync(first.Id, "Reception printer", null, null);

        // Antwortet die alte Adresse noch (zweite Adresse desselben Geräts), entsteht ein eigener Eintrag.
        Assert.Equal(1, await service.StoreAsync(range.Id, [
            new DeviceObservation { IpAddress = "10.0.0.50", PingReply = true, MacAddress = "30:05:5C:12:34:56" },
            new DeviceObservation { IpAddress = "10.0.0.51", PingReply = true, MacAddress = "30:05:5C:12:34:56" }], default));
        await service.DeleteAsync((await service.ListAsync()).Single(d => d.IpAddress == "10.0.0.51").Id);

        Assert.Equal(0, await service.StoreAsync(range.Id, [new DeviceObservation { IpAddress = "10.0.0.77", PingReply = true, MacAddress = "30:05:5C:12:34:56", OpenPorts = [9100] }], default));
        var moved = Assert.Single(await service.ListAsync());
        Assert.Equal("10.0.0.77", moved.IpAddress);
        Assert.Equal("Reception printer", moved.DisplayName);
        Assert.Equal("30:05:5C:12:34:56", moved.MacAddress);
    }

    [Fact]
    public async Task A_missing_snmp_reply_keeps_the_classification()
    {
        using var db = new TestDb();
        var service = new DeviceService(db, new SimulatedDeviceProbe(), new ReversingProtector());
        await service.StoreAsync(null, [new DeviceObservation { IpAddress = "10.0.0.2", PingReply = true, OpenPorts = [22, 443], Snmp = new() { SysDescr = "Aruba 2930F Switch", BridgePorts = 28, Model = "2930F" } }], default);
        await service.StoreAsync(null, [new DeviceObservation { IpAddress = "10.0.0.2", PingReply = true, OpenPorts = [22, 443] }], default);
        var device = Assert.Single(await service.ListAsync());
        Assert.Equal(DeviceKind.Switch, device.Kind);
        Assert.Equal("2930F", device.Model);
        Assert.Equal("22,443", device.OpenPorts);
    }

    private sealed class RecordingProbe : IDeviceProbe
    {
        public Dictionary<string, IReadOnlyList<string>> Seen { get; } = [];

        public Task<DeviceObservation> ProbeAsync(IPAddress address, IReadOnlyList<string> communities, CancellationToken ct = default)
        {
            lock (Seen)
                Seen[address.ToString()] = communities;
            return Task.FromResult(new DeviceObservation { IpAddress = address.ToString(), PingReply = true });
        }

        public Task<TraceResult> TraceAsync(IPAddress target, CancellationToken ct = default) => Task.FromResult(new TraceResult([], true));

        public IReadOnlyDictionary<string, string> LocalNeighbors() => new Dictionary<string, string>();
    }
}
