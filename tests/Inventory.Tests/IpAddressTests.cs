using System.Buffers.Binary;
using System.Net;
using System.Text;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public class IpAddressTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_strongest_evidence_decides_the_status()
    {
        Assert.Equal(IpAddressStatus.Free, IpAddressRules.Classify([]));
        Assert.Equal(IpAddressStatus.Stale, IpAddressRules.Classify([new(IpEvidenceKind.PtrRecord, "old", null, IpAddressStatus.Stale)]));
        Assert.Equal(IpAddressStatus.DnsOnly, IpAddressRules.Classify([new(IpEvidenceKind.PtrRecord, "old", null, IpAddressStatus.Stale), new(IpEvidenceKind.ARecord, "srv", null, IpAddressStatus.DnsOnly)]));
        Assert.Equal(IpAddressStatus.InUse, IpAddressRules.Classify([new(IpEvidenceKind.ARecord, "srv", null, IpAddressStatus.DnsOnly), new(IpEvidenceKind.Ping, null, Now, IpAddressStatus.InUse)]));
        Assert.Equal(IpAddressStatus.Excluded, IpAddressRules.Classify([new(IpEvidenceKind.Ping, null, Now, IpAddressStatus.InUse), new(IpEvidenceKind.Excluded, null, null, IpAddressStatus.Excluded)]));
        Assert.Equal(IpAddressStatus.Reserved, IpAddressRules.Classify([new(IpEvidenceKind.Excluded, null, null, IpAddressStatus.Excluded), new(IpEvidenceKind.Reservation, null, Now, IpAddressStatus.Reserved)]));

        Assert.Equal(IpAddressStatus.InUse, IpAddressRules.BySighting(Now.AddDays(-29), Now));
        Assert.Equal(IpAddressStatus.Stale, IpAddressRules.BySighting(Now.AddDays(-31), Now));
        Assert.Equal(IpAddressStatus.Stale, IpAddressRules.BySighting(null, Now));
        Assert.Equal(IpAddressStatus.DnsOnly, IpAddressRules.ByDnsRecord(null, Now));
        Assert.Equal(IpAddressStatus.DnsOnly, IpAddressRules.ByDnsRecord(Now.AddDays(-10), Now));
        Assert.Equal(IpAddressStatus.Stale, IpAddressRules.ByDnsRecord(Now.AddDays(-90), Now));
    }

    [Fact]
    public void Pick_finds_single_addresses_and_contiguous_blocks_from_either_end()
    {
        uint[] candidates = [5, 6, 8, 9, 10, 20, 21, 22];
        Assert.Equal([5u, 6u], IpAddressRules.Pick(candidates, 2, contiguous: false, fromTop: false));
        Assert.Equal([21u, 22u], IpAddressRules.Pick(candidates, 2, contiguous: false, fromTop: true));
        Assert.Equal([8u, 9u, 10u], IpAddressRules.Pick(candidates, 3, contiguous: true, fromTop: false));
        Assert.Equal([20u, 21u, 22u], IpAddressRules.Pick(candidates, 3, contiguous: true, fromTop: true));
        Assert.Empty(IpAddressRules.Pick(candidates, 4, contiguous: true, fromTop: false));
        Assert.Empty(IpAddressRules.Pick(candidates, 9, contiguous: false, fromTop: false));
        Assert.Empty(IpAddressRules.Pick(candidates, 0, contiguous: false, fromTop: false));
    }

    [Fact]
    public void Dns_records_from_the_directory_are_parsed()
    {
        var a = DnsRecordParser.Parse(Record(DnsRecordParser.TypeA, [10, 0, 1, 5], hours: 3_740_000));
        Assert.Equal(DnsRecordParser.TypeA, a!.Value.Type);
        Assert.Equal("10.0.1.5", a.Value.Value);
        Assert.Equal(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(3_740_000), a.Value.RefreshedUtc);

        var ptr = DnsRecordParser.Parse(Record(DnsRecordParser.TypePtr, CountName("pc5", "corp", "example"), hours: 0));
        Assert.Equal("pc5.corp.example", ptr!.Value.Value);
        Assert.Null(ptr.Value.RefreshedUtc);

        // Andere Typen (hier MX), falsche Version und abgeschnittene Daten ergeben nichts.
        Assert.Null(DnsRecordParser.Parse(Record(15, [0, 10], hours: 0)));
        var wrongVersion = Record(DnsRecordParser.TypeA, [10, 0, 1, 5], hours: 0);
        wrongVersion[4] = 4;
        Assert.Null(DnsRecordParser.Parse(wrongVersion));
        Assert.Null(DnsRecordParser.Parse(Record(DnsRecordParser.TypeA, [10, 0, 1, 5], hours: 0)[..26]));

        Assert.Equal("10.0.1.5", DnsRecordParser.ReverseAddress("5.1", "0.10.in-addr.arpa"));
        Assert.Equal("10.0.1.5", DnsRecordParser.ReverseAddress("5", "1.0.10.in-addr.arpa"));
        Assert.Null(DnsRecordParser.ReverseAddress("@", "1.0.10.in-addr.arpa"));
        Assert.Null(DnsRecordParser.ReverseAddress("pc5", "corp.example"));

        Assert.Equal(["pc,5", "corp.example", "MicrosoftDNS", "DomainDnsZones", "corp", "example"],
            LdapDnsZoneSource.Rdns(@"DC=pc\,5,DC=corp.example,CN=MicrosoftDNS,DC=DomainDnsZones,DC=corp,DC=example"));
        Assert.Equal(["ä"], LdapDnsZoneSource.Rdns(@"DC=\C3\A4"));
    }

    [Fact]
    public async Task A_scan_combines_ping_dns_and_inventory_into_a_status_per_address()
    {
        using var db = new TestDb();
        var probe = new FakeProbe { Answering = { "10.0.1.1", "10.0.1.2" } };
        var zones = new FakeZones(new DnsZoneData(
            [
                new("srv-off.corp.example", "10.0.1.3", null),
                new("pc5.corp.example", "10.0.1.5", Now.AddDays(-2)),
                new("retired.corp.example", "10.0.1.6", Now.AddDays(-100)),
            ],
            [
                new("old.corp.example", "10.0.1.4", Now.AddDays(-400)),
                new("pc5.corp.example", "10.0.1.5", Now.AddDays(-2)),
            ], 2));
        var service = Service(db, probe, zones);
        var subnet = await service.SaveSubnetAsync(null, "Servers", "10.0.1.0/28", 10, "10.0.1.1", "10.0.1.10-12", null, true, 12);
        await AddComputerAsync(db, "pc-07", "10.0.1.7", Now.AddHours(-1));
        await AddComputerAsync(db, "pc-08", "10.0.1.8", Now.AddDays(-60));
        await service.ReserveAsync(subnet.Id, ["10.0.1.9"], "new SQL server", null, "anna");

        var result = await service.ScanAsync(subnet.Id);
        Assert.Contains("2 of 14 addresses answered", result);

        var view = (await service.GetViewAsync(subnet.Id))!;
        string Status(int octet) => view.Addresses.Single(a => a.Address == $"10.0.1.{octet}").Status.ToString();
        Assert.Equal("InUse", Status(1));
        Assert.Equal("InUse", Status(2));
        Assert.Equal("DnsOnly", Status(3));
        Assert.Equal("Stale", Status(4));
        Assert.Equal("DnsOnly", Status(5));
        Assert.Equal("Stale", Status(6));
        Assert.Equal("InUse", Status(7));
        Assert.Equal("Stale", Status(8));
        Assert.Equal("Reserved", Status(9));
        Assert.Equal("Excluded", Status(10));
        Assert.Equal("Free", Status(13));
        Assert.Equal("Free", Status(14));
        Assert.Equal(2, view.Count(IpAddressStatus.Free));

        var gateway = view.Addresses.Single(a => a.Address == "10.0.1.1");
        Assert.Contains(gateway.Evidence, e => e.Kind == IpEvidenceKind.Gateway);
        Assert.Contains(gateway.Evidence, e => e.Kind == IpEvidenceKind.Ping);
        var orphaned = view.Addresses.Single(a => a.Address == "10.0.1.4").Evidence.Single();
        Assert.Equal((IpEvidenceKind.PtrRecord, "old.corp.example"), (orphaned.Kind, orphaned.Text));
        // Der PTR-Name stand nicht im AD; nur dann fragt der Scan den Resolver.
        Assert.Equal(["old.corp.example"], probe.Forwarded);
        Assert.Contains(view.Addresses.Single(a => a.Address == "10.0.1.7").Evidence, e => e.Kind == IpEvidenceKind.Computer && e.Text == "pc-07");
    }

    [Fact]
    public async Task Finding_free_addresses_skips_candidates_that_answer_now()
    {
        using var db = new TestDb();
        var probe = new FakeProbe();
        var service = Service(db, probe, new FakeZones(new DnsZoneData([], [new("old.corp.example", "10.0.2.5", Now.AddDays(-400))], 1)));
        var subnet = await service.SaveSubnetAsync(null, "Clients", "10.0.2.0/29", null, null, null, null, true, 12);
        await service.ScanAsync(subnet.Id);

        Assert.Equal(["10.0.2.1", "10.0.2.2", "10.0.2.3"], (await service.FindFreeAsync(subnet.Id, 3, contiguous: true, includeStale: false, fromTop: false)).Addresses);
        Assert.Equal(["10.0.2.6"], (await service.FindFreeAsync(subnet.Id, 1, contiguous: true, includeStale: false, fromTop: true)).Addresses);
        // .5 hat nur einen alten PTR: ohne „veraltete anbieten“ liegt der oberste Block aus drei darunter.
        Assert.Equal(["10.0.2.2", "10.0.2.3", "10.0.2.4"], (await service.FindFreeAsync(subnet.Id, 3, contiguous: true, includeStale: false, fromTop: true)).Addresses);
        Assert.Equal(["10.0.2.4", "10.0.2.5", "10.0.2.6"], (await service.FindFreeAsync(subnet.Id, 3, contiguous: true, includeStale: true, fromTop: true)).Addresses);

        // Ein neuer Rechner hat sich .6 genommen: er antwortet bei der Nachprüfung und wird übersprungen und vermerkt.
        probe.Answering.Add("10.0.2.6");
        var free = await service.FindFreeAsync(subnet.Id, 1, contiguous: false, includeStale: false, fromTop: true);
        Assert.Equal(["10.0.2.4"], free.Addresses);
        Assert.Equal(["10.0.2.6"], free.Answered);
        Assert.Equal(IpAddressStatus.InUse, (await service.GetViewAsync(subnet.Id))!.Addresses.Single(a => a.Address == "10.0.2.6").Status);

        Assert.Empty((await service.FindFreeAsync(subnet.Id, 6, contiguous: false, includeStale: false, fromTop: false)).Addresses);
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindFreeAsync(subnet.Id, 0, false, false, false));
    }

    [Fact]
    public async Task Without_the_directory_the_resolver_supplies_reverse_and_forward_names()
    {
        using var db = new TestDb();
        var probe = new FakeProbe
        {
            Pointers = { ["10.0.3.4"] = "pc4.corp.example", ["10.0.3.5"] = "gone.corp.example" },
            Names = { ["pc4.corp.example"] = ["10.0.3.4"] },
        };
        var service = Service(db, probe, new FakeZones(null));
        var subnet = await service.SaveSubnetAsync(null, "Lab", "10.0.3.0/29", null, null, null, null, true, 12);

        var result = await service.ScanAsync(subnet.Id);
        Assert.Contains("DNS zones from Active Directory not readable (no domain)", result);
        var view = (await service.GetViewAsync(subnet.Id))!;
        Assert.Contains("DNS via the resolver.", view.Subnet.LastResult);
        var confirmed = view.Addresses.Single(a => a.Address == "10.0.3.4");
        Assert.Equal(IpAddressStatus.DnsOnly, confirmed.Status);
        Assert.Contains(confirmed.Evidence, e => e.Kind == IpEvidenceKind.ARecord && e.Text == "pc4.corp.example");
        Assert.Equal(IpAddressStatus.Stale, view.Addresses.Single(a => a.Address == "10.0.3.5").Status);

        // Verschwinden die Einträge, verschwinden auch die Beobachtungen ohne andere Spur.
        probe.Pointers.Clear();
        await service.ScanAsync(subnet.Id);
        await using var context = db.CreateDbContext();
        Assert.Empty(await context.IpObservations.ToListAsync());
    }

    [Fact]
    public async Task Reservations_are_unique_inside_the_subnet_and_expire()
    {
        using var db = new TestDb();
        var clock = new Clock(Now);
        var service = Service(db, new FakeProbe(), new FakeZones(new DnsZoneData([], [], 0)), clock);
        var subnet = await service.SaveSubnetAsync(null, "Servers", "10.0.4.0/29", null, null, null, null, true, 12);

        var reserved = await service.ReserveAsync(subnet.Id, ["10.0.4.3", "10.0.4.2"], "  cluster  ", Now.AddDays(3), "anna");
        Assert.Equal(["10.0.4.2", "10.0.4.3"], reserved.Select(r => r.Address));
        Assert.All(reserved, r => Assert.Equal(("cluster", "anna"), (r.Note, r.ReservedBy)));

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReserveAsync(subnet.Id, ["10.0.4.3", "10.0.4.4"], null, null, "ben"));
        Assert.Contains("10.0.4.3", duplicate.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReserveAsync(subnet.Id, ["10.0.5.1"], null, null, "ben"));
        // Netz- und Broadcast-Adresse sind nicht nutzbar.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReserveAsync(subnet.Id, ["10.0.4.7"], null, null, "ben"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReserveAsync(subnet.Id, ["10.0.4.4"], null, Now.AddMinutes(-1), "ben"));

        var free = await service.ReserveFreeAsync(subnet.Id, 2, contiguous: true, "web", null, "api");
        Assert.Equal(["10.0.4.4", "10.0.4.5"], free.Select(r => r.Address));

        Assert.Empty(await service.CleanupReservationsAsync());
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(["10.0.4.2", "10.0.4.3"], (await service.CleanupReservationsAsync()).Select(r => r.Address).Order());
        Assert.Equal(["10.0.4.4", "10.0.4.5"], (await service.ListReservationsAsync(subnet.Id)).Select(r => r.Address));

        var release = (await service.ListReservationsAsync(subnet.Id))[0];
        Assert.Equal("10.0.4.4", (await service.ReleaseAsync(release.Id))!.Address);
        Assert.Null(await service.ReleaseAsync(release.Id));
    }

    [Fact]
    public async Task Subnets_are_validated_and_must_not_overlap()
    {
        using var db = new TestDb();
        var service = Service(db, new FakeProbe(), new FakeZones(new DnsZoneData([], [], 0)));

        var subnet = await service.SaveSubnetAsync(null, "Office", "10.0.6.77/24", null, null, null, null, true, 12);
        Assert.Equal("10.0.6.0/24", subnet.Cidr);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSubnetAsync(null, "Big", "10.1.0.0/19", null, null, null, null, true, 12));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSubnetAsync(null, "Tiny", "10.1.0.0/31", null, null, null, null, true, 12));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSubnetAsync(null, "Text", "office", null, null, null, null, true, 12));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSubnetAsync(null, "Gw", "10.1.0.0/24", null, "10.2.0.1", null, null, true, 12));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSubnetAsync(null, "Ex", "10.1.0.0/24", null, null, "10.1.0.x", null, true, 12));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSubnetAsync(null, "Vlan", "10.1.0.0/24", 5000, null, null, null, true, 12));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSubnetAsync(null, " ", "10.1.0.0/24", null, null, null, null, true, 12));
        var overlap = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSubnetAsync(null, "Part", "10.0.6.128/25", null, null, null, null, true, 12));
        Assert.Contains("Office", overlap.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSubnetAsync(null, "office", "10.0.7.0/24", null, null, null, null, true, 12));

        // Ein anderes Netz verwirft die alten Beobachtungen und die Reservierungen außerhalb.
        await service.ReserveAsync(subnet.Id, ["10.0.6.10", "10.0.6.200"], null, null, "anna");
        await using (var context = db.CreateDbContext())
        {
            context.IpObservations.Add(new IpObservation { SubnetId = subnet.Id, Address = "10.0.6.10", FirstSeenUtc = Now, LastReplyUtc = Now });
            await context.SaveChangesAsync();
        }
        await service.SaveSubnetAsync(subnet.Id, "Office", "10.0.6.0/25", null, null, null, null, true, 12);
        await using (var context = db.CreateDbContext())
        {
            Assert.Empty(await context.IpObservations.ToListAsync());
            Assert.Equal(["10.0.6.10"], await context.IpReservations.Select(r => r.Address).ToListAsync());
        }
    }

    [Fact]
    public async Task Suggestions_come_from_the_adapters_and_leave_out_existing_subnets()
    {
        using var db = new TestDb();
        var service = Service(db, new FakeProbe(), new FakeZones(new DnsZoneData([], [], 0)));
        var computers = await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01 pc-02 pc-03");
        await using (var context = db.CreateDbContext())
        {
            context.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computers[0].Id, Name = "LAN", IpAddresses = "10.0.8.10", SubnetMasks = "255.255.255.0" });
            context.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computers[1].Id, Name = "LAN", IpAddresses = "10.0.9.10, 169.254.1.1", SubnetMasks = "255.255.255.0, 255.255.0.0" });
            context.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computers[2].Id, Name = "VPN", IpAddresses = "10.8.0.2", SubnetMasks = "255.0.0.0" });
            await context.SaveChangesAsync();
        }
        await service.SaveSubnetAsync(null, "Existing", "10.0.9.0/24", null, null, null, null, true, 12);

        var suggestion = Assert.Single(await service.SuggestSubnetsAsync());
        Assert.Equal(("10.0.8.0/24", 1), (suggestion.Cidr, suggestion.Hosts));
    }

    [Fact]
    public async Task Only_due_subnets_are_scanned_by_the_scheduler()
    {
        using var db = new TestDb();
        var clock = new Clock(Now);
        var probe = new FakeProbe();
        var service = Service(db, probe, new FakeZones(new DnsZoneData([], [], 0)), clock);
        await service.SaveSubnetAsync(null, "A", "10.0.10.0/30", null, null, null, null, true, 6);
        await service.SaveSubnetAsync(null, "B", "10.0.11.0/30", null, null, null, null, false, 6);

        Assert.NotNull(await service.ScanDueAsync());
        Assert.Equal(2, probe.Pinged.Count);
        Assert.Null(await service.ScanDueAsync());
        clock.Advance(TimeSpan.FromHours(6));
        Assert.NotNull(await service.ScanDueAsync());
        Assert.Equal(4, probe.Pinged.Count);
        Assert.DoesNotContain(probe.Pinged, a => a.StartsWith("10.0.11."));
    }

    [Fact]
    public async Task A_rejected_edit_keeps_observations_and_reservations()
    {
        using var db = new TestDb();
        var service = Service(db, new FakeProbe(), new FakeZones(new DnsZoneData([], [], 0)));
        var office = await service.SaveSubnetAsync(null, "Office", "10.0.12.0/24", null, null, null, null, true, 12);
        await service.SaveSubnetAsync(null, "Lab", "10.0.13.0/24", null, null, null, null, true, 12);
        await service.ReserveAsync(office.Id, ["10.0.12.200"], null, null, "anna");
        await using (var context = db.CreateDbContext())
        {
            context.IpObservations.Add(new IpObservation { SubnetId = office.Id, Address = "10.0.12.10", FirstSeenUtc = Now, LastReplyUtc = Now });
            await context.SaveChangesAsync();
        }

        // 10.0.12.0/23 überschneidet sich mit Lab: abgelehnt, und nichts ist verloren.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSubnetAsync(office.Id, "Office", "10.0.12.0/23", null, null, null, null, true, 12));
        await using (var context = db.CreateDbContext())
        {
            Assert.Single(await context.IpObservations.ToListAsync());
            Assert.Single(await context.IpReservations.ToListAsync());
            Assert.Equal("10.0.12.0/24", (await context.IpSubnets.FindAsync(office.Id))!.Cidr);
        }
    }

    [Fact]
    public async Task The_free_search_goes_on_until_the_candidates_run_out()
    {
        using var db = new TestDb();
        var probe = new FakeProbe();
        var service = Service(db, probe, new FakeZones(new DnsZoneData([], [], 0)));
        var subnet = await service.SaveSubnetAsync(null, "Busy", "10.0.14.0/29", null, null, null, null, true, 12);
        await service.ScanAsync(subnet.Id);

        // Seit dem Scan sind .1 bis .5 in Betrieb gegangen; nur .6 ist wirklich frei.
        foreach (var octet in Enumerable.Range(1, 5))
            probe.Answering.Add($"10.0.14.{octet}");
        var free = await service.FindFreeAsync(subnet.Id, 1, contiguous: false, includeStale: false, fromTop: false);
        Assert.Equal(["10.0.14.6"], free.Addresses);
        Assert.Equal(5, free.Answered.Count);
    }

    [Fact]
    public async Task Scanning_all_subnets_on_demand_includes_those_without_regular_scans()
    {
        using var db = new TestDb();
        var probe = new FakeProbe();
        var service = Service(db, probe, new FakeZones(new DnsZoneData([], [], 0)));
        await service.SaveSubnetAsync(null, "A", "10.0.15.0/30", null, null, null, null, true, 12);
        await service.SaveSubnetAsync(null, "B", "10.0.16.0/30", null, null, null, null, false, 12);

        await service.ScanAsync();
        Assert.Contains(probe.Pinged, a => a.StartsWith("10.0.16."));
    }

    private static IpAddressService Service(TestDb db, IAddressProbe probe, IDnsZoneSource zones, TimeProvider? clock = null) =>
        new(db, probe, zones, new SettingsService(db), new CredentialService(db, new ReversingProtector()), clock ?? new Clock(Now));

    private static async Task AddComputerAsync(TestDb db, string name, string ip, DateTime seen)
    {
        await using var context = db.CreateDbContext();
        var computer = new Computer { Name = name, LastSeenUtc = seen };
        context.Computers.Add(computer);
        await context.SaveChangesAsync();
        context.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computer.Id, Name = "LAN", IpAddresses = ip, SubnetMasks = "255.255.255.0" });
        await context.SaveChangesAsync();
    }

    private static byte[] Record(ushort type, byte[] data, uint hours)
    {
        var record = new byte[24 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), type);
        record[4] = 5;
        record[5] = 0xF0;
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(12), 1200);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), hours);
        data.CopyTo(record, 24);
        return record;
    }

    private static byte[] CountName(params string[] labels)
    {
        var raw = labels.SelectMany(l => new[] { (byte)l.Length }.Concat(Encoding.ASCII.GetBytes(l))).Append((byte)0).ToArray();
        return [(byte)raw.Length, (byte)labels.Length, .. raw];
    }

    private sealed class FakeProbe : IAddressProbe
    {
        public HashSet<string> Answering { get; } = [];
        public Dictionary<string, string> Pointers { get; } = [];
        public Dictionary<string, string[]> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Forwarded { get; } = [];
        public List<string> Pinged { get; } = [];

        public Task<bool> PingAsync(IPAddress address, CancellationToken ct = default)
        {
            lock (Pinged)
                Pinged.Add(address.ToString());
            return Task.FromResult(Answering.Contains(address.ToString()));
        }

        public Task<string?> ReverseAsync(IPAddress address, CancellationToken ct = default) => Task.FromResult(Pointers.GetValueOrDefault(address.ToString()));

        public Task<IReadOnlyList<string>> ForwardAsync(string name, CancellationToken ct = default)
        {
            lock (Forwarded)
                Forwarded.Add(name);
            return Task.FromResult<IReadOnlyList<string>>(Names.GetValueOrDefault(name) ?? []);
        }

        public IReadOnlyDictionary<string, string> LocalNeighbors() => new Dictionary<string, string>();
    }

    /// <param name="data">null = das Verzeichnis ist nicht erreichbar.</param>
    private sealed class FakeZones(DnsZoneData? data) : IDnsZoneSource
    {
        public Task<DnsZoneData> ReadAsync(DnsZoneQuery query, CancellationToken ct = default) =>
            data is null ? throw new RemoteException("no domain") : Task.FromResult(data);
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        private DateTimeOffset _now = new(now);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
