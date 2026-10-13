using System.Net;
using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public sealed class WakeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
    private readonly SimulatedWakeSender _sender = new();
    private readonly HashSet<string> _offline = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComputerService _computers;
    private readonly PackageService _packages;
    private readonly WakeService _wake;
    private readonly RecordingAgents _agents = new();

    /// <summary>Stellt den Weg über die Agenten dar und merkt sich, welcher Rechner welche MAC-Adressen wecken soll.</summary>
    private sealed class RecordingAgents : IRemoteTransport
    {
        public TransportKind Kind => TransportKind.Agent;
        public List<(string Host, JobStep Step)> Orders { get; } = [];

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            lock (Orders)
                Orders.Add((target.Host, job.Steps.Single()));
            return Task.FromResult(new JobResult { JobId = job.JobId, Success = true });
        }
    }

    /// <summary>Ein Rechner mit Agent, der sich gerade gemeldet hat (oder vor <paramref name="minutesAgo"/> Minuten).</summary>
    private async Task<Computer> AgentAsync(string name, string ip, string? mask = "255.255.255.0", int minutesAgo = 0)
    {
        var computer = await ComputerAsync(name, mac: "00:1A:2B:00:00:" + ip.Split('.')[3].PadLeft(2, '0')[..2], ip: ip, mask: mask);
        await using var db = _db.CreateDbContext();
        var stored = db.Computers.Single(c => c.Id == computer.Id);
        (stored.AgentKeyHash, stored.AgentLastSeenUtc) = ("hash-" + name, DateTime.UtcNow.AddMinutes(-minutesAgo));
        await db.SaveChangesAsync();
        return computer;
    }

    public WakeTests()
    {
        _computers = new ComputerService(_db, new SimulatedHostProbe());
        _packages = new PackageService(_db, new PackageRepository(_repoDir));
        _wake = new WakeService(_db, _sender, [new SimulatedTransport(TransportKind.SmbService), _agents]);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    private DeploymentService CreateDeployments() =>
        new(_db, _packages, new RemoteJobService([new SimulatedTransport(TransportKind.SmbService)], _computers, new CredentialService(_db, new ReversingProtector()), new SettingsService(_db)),
            new InventoryStore(_db), new FixedProbe(host => new ProbeResult(!_offline.Contains(host), "10.0.0.1")), wake: _wake);

    private async Task<Computer> ComputerAsync(string name, string? mac = "00:1A:2B:3C:4D:5E", string ip = "10.20.30.40", string? mask = "255.255.252.0")
    {
        var computer = (await _computers.ImportAsync(name)).Single();
        await using var db = _db.CreateDbContext();
        db.Computers.Single(c => c.Id == computer.Id).IpAddress = ip;
        if (mac is not null)
            db.NetworkAdapters.Add(new ComputerNetworkAdapter { ComputerId = computer.Id, Name = "Ethernet", MacAddress = mac, IpAddresses = ip, SubnetMasks = mask });
        await db.SaveChangesAsync();
        return computer;
    }

    private async Task<int> PackageAsync()
    {
        var package = await _packages.SaveAsync(null, "Setup", null, null);
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Setup", Type = StepType.Command, Script = "echo ok" }, null);
        return package.Id;
    }

    private static async Task<Deployment> WaitAsync(DeploymentService deployments, int id)
    {
        for (var i = 0; i < 200; i++)
        {
            var deployment = await deployments.GetAsync(id);
            if (deployment!.FinishedUtc is not null && !deployments.IsRunning(id))
                return deployment;
            await Task.Delay(50);
        }
        throw new TimeoutException("Deployment wurde nicht fertig");
    }

    [Theory]
    [InlineData("00:1A:2B:3C:4D:5E")]
    [InlineData("00-1a-2b-3c-4d-5e")]
    [InlineData("001A.2B3C.4D5E")]
    [InlineData(" 001a2b3c4d5e ")]
    public void Mac_addresses_are_read_in_the_usual_notations(string text) =>
        Assert.Equal("00:1A:2B:3C:4D:5E", WakeOnLan.NormalizeMac(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00:1A:2B:3C:4D")]
    [InlineData("00:1A:2B:3C:4D:ZZ")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    [InlineData("+0:1A:2B:3C:4D:5E")]
    public void Anything_else_is_not_a_mac_address(string? text) => Assert.Null(WakeOnLan.ParseMac(text));

    [Fact]
    public void The_magic_packet_is_six_times_ff_and_sixteen_times_the_address()
    {
        var mac = WakeOnLan.ParseMac("00:1A:2B:3C:4D:5E")!;
        var packet = WakeOnLan.MagicPacket(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
            Assert.Equal(mac, packet.Skip(6 + i * 6).Take(6));
    }

    [Theory]
    [InlineData("10.20.30.40", "255.255.252.0", "10.20.31.255")]
    [InlineData("192.168.1.17", "255.255.255.0", "192.168.1.255")]
    [InlineData("192.168.1.17", null, "192.168.1.255")]
    // Eine Präfixlänge (so meldet Windows IPv6) ist keine Maske: es bleibt bei der Annahme /24.
    [InlineData("192.168.1.17", "64", "192.168.1.255")]
    public void The_broadcast_address_follows_the_subnet_mask(string address, string? mask, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), WakeOnLan.Broadcast(address, mask));

    [Fact]
    public void Ipv6_addresses_have_no_broadcast() => Assert.Null(WakeOnLan.Broadcast("fe80::1", "64"));

    [Fact]
    public void The_plan_covers_every_adapter_broadcasts_first()
    {
        var plan = WakeOnLan.PlanFor(null, "10.20.30.40",
        [
            new WakeOnLan.Adapter("00:1A:2B:3C:4D:5E", ["10.20.30.40", "fe80::1"], ["255.255.252.0", "64"]),
            new WakeOnLan.Adapter("00-1A-2B-3C-4D-5F", ["192.168.7.9"], []),
            new WakeOnLan.Adapter(null, [], []),
        ]);

        Assert.Equal(["00:1A:2B:3C:4D:5E", "00:1A:2B:3C:4D:5F"], plan.Macs.Select(WakeOnLan.Format));
        Assert.Equal(["10.20.31.255", "192.168.7.255", "10.20.30.40", "192.168.7.9"], plan.Addresses.Select(a => a.ToString()));
    }

    [Fact]
    public void An_entered_mac_address_replaces_those_of_the_adapters()
    {
        var plan = WakeOnLan.PlanFor("AA-BB-CC-DD-EE-01", "10.0.5.6", [new WakeOnLan.Adapter("00:1A:2B:3C:4D:5E", ["10.0.5.6"], ["255.255.255.0"])]);

        Assert.Equal(["AA:BB:CC:DD:EE:01"], plan.Macs.Select(WakeOnLan.Format));
        Assert.Equal(["10.0.5.255", "10.0.5.6"], plan.Addresses.Select(a => a.ToString()));
    }

    [Fact]
    public async Task Waking_sends_a_packet_per_mac_address()
    {
        var computer = await ComputerAsync("pc-01");

        var result = await _wake.WakeAsync(computer.Id);

        Assert.True(result!.Sent);
        Assert.Equal(["00:1A:2B:3C:4D:5E"], result.Macs);
        var (packet, addresses) = Assert.Single(_sender.Sent);
        Assert.Equal(WakeOnLan.MagicPacket(WakeOnLan.ParseMac("00:1A:2B:3C:4D:5E")!), packet);
        Assert.Equal(["10.20.31.255", "10.20.30.40"], addresses.Select(a => a.ToString()));
    }

    [Fact]
    public async Task Without_a_known_mac_address_nothing_is_sent()
    {
        var computer = await ComputerAsync("pc-01", mac: null);

        var result = await _wake.WakeAsync(computer.Id);

        Assert.False(result!.Sent);
        Assert.Empty(result.Macs);
        Assert.Empty(_sender.Sent);
        Assert.Null(await _wake.WakeAsync(4711));
    }

    [Fact]
    public async Task The_entered_mac_address_is_validated_and_normalized()
    {
        var computer = await ComputerAsync("pc-01", mac: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _computers.SetWakeMacAsync(computer.Id, "nonsense"));
        await _computers.SetWakeMacAsync(computer.Id, "aa-bb-cc-dd-ee-01");
        Assert.Equal("AA:BB:CC:DD:EE:01", (await _computers.GetAsync(computer.Id))!.WakeMac);
        Assert.True((await _wake.WakeAsync(computer.Id))!.Sent);

        await _computers.SetWakeMacAsync(computer.Id, " ");
        Assert.Null((await _computers.GetAsync(computer.Id))!.WakeMac);
    }

    [Fact]
    public async Task A_deployment_wakes_unreachable_computers_and_runs_them_once_they_answer()
    {
        var off = await ComputerAsync("pc-off");
        var on = await ComputerAsync("pc-on", mac: "00:1A:2B:3C:4D:60", ip: "10.20.30.41");
        _offline.Add("pc-off");
        var deployments = CreateDeployments();
        var before = DateTime.UtcNow;

        // Wecken schließt das Warten ein, auch wenn es nicht eigens verlangt ist.
        var started = await WaitAsync(deployments, (await deployments.StartAsync(await PackageAsync(), [off.Id, on.Id], new DeploymentOptions(false) { Wake = true })).Id);

        Assert.True(started.WakeTargets);
        Assert.Equal(TargetStatus.Succeeded, started.Targets.Single(t => t.ComputerName == "pc-on").Status);
        var waiting = started.Targets.Single(t => t.ComputerName == "pc-off");
        Assert.Equal(TargetStatus.WaitingOffline, waiting.Status);
        Assert.Contains("wake-up sent", waiting.Error);
        // Nur der Rechner, der nicht antwortet, wird geweckt.
        Assert.Single(_sender.Sent);

        // Die Nachprüfung ist erst fällig, wenn der Rechner Zeit zum Hochfahren hatte, und nur einmal.
        Assert.False(deployments.TakeWakeRecheck(before));
        Assert.True(deployments.TakeWakeRecheck(DateTime.UtcNow + WakeService.BootWait));
        Assert.False(deployments.TakeWakeRecheck(DateTime.UtcNow + WakeService.BootWait));

        // Bleibt er aus, wird er nicht bei jedem Durchlauf erneut geweckt.
        Assert.Equal(0, await deployments.RetryWaitingAsync());
        Assert.Single(_sender.Sent);

        _offline.Clear();
        Assert.Equal(1, await deployments.RetryWaitingAsync());
        var done = await WaitAsync(deployments, started.Id);
        Assert.All(done.Targets, t => Assert.Equal(TargetStatus.Succeeded, t.Status));
    }

    [Fact]
    public async Task Without_the_option_nobody_is_woken()
    {
        var off = await ComputerAsync("pc-off");
        _offline.Add("pc-off");
        var deployments = CreateDeployments();

        var started = await WaitAsync(deployments, (await deployments.StartAsync(await PackageAsync(), [off.Id], new DeploymentOptions(false, RetryOffline: true))).Id);

        Assert.Equal(TargetStatus.WaitingOffline, started.Targets.Single().Status);
        Assert.Equal(0, await deployments.RetryWaitingAsync());
        Assert.Empty(_sender.Sent);
        Assert.False(deployments.TakeWakeRecheck(DateTime.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task A_computer_without_mac_address_just_waits()
    {
        var off = await ComputerAsync("pc-off", mac: null);
        _offline.Add("pc-off");
        var deployments = CreateDeployments();

        var started = await WaitAsync(deployments, (await deployments.StartAsync(await PackageAsync(), [off.Id], new DeploymentOptions(false) { Wake = true })).Id);

        var target = started.Targets.Single();
        Assert.Equal(TargetStatus.WaitingOffline, target.Status);
        Assert.DoesNotContain("wake-up", target.Error);
    }

    [Fact]
    public async Task An_agent_in_the_subnet_of_the_target_is_asked_to_send_the_packet_there()
    {
        var off = await ComputerAsync("pc-off", ip: "10.20.30.40", mask: "255.255.255.0");
        await AgentAsync("pc-near", "10.20.30.77");
        await AgentAsync("pc-far", "10.20.31.77");

        var result = await _wake.WakeAsync(off.Id);
        await result!.Relayed;

        Assert.Equal(["pc-near"], result!.Relays);
        var (host, step) = Assert.Single(_agents.Orders);
        Assert.Equal("pc-near", host);
        Assert.Equal(StepType.WakeOnLan, step.Type);
        Assert.Equal("00:1A:2B:3C:4D:5E", step.Script);
        // Der Server sendet weiterhin selbst.
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task The_subnet_is_judged_by_the_mask_of_the_agent()
    {
        // Das Ziel wurde mit einem älteren Runner gescannt (keine Maske); der Agent kennt sein /22-Netz.
        var off = await ComputerAsync("pc-off", ip: "10.20.31.40", mask: null);
        await AgentAsync("pc-wide", "10.20.28.5", mask: "255.255.252.0");
        await AgentAsync("pc-other", "10.20.32.5", mask: "255.255.252.0");

        var result = await _wake.WakeAsync(off.Id);
        await result!.Relayed;

        Assert.Equal(["pc-wide"], result!.Relays);
    }

    [Fact]
    public async Task An_agent_whose_scan_reported_no_ipv4_address_counts_with_its_last_seen_address()
    {
        var off = await ComputerAsync("pc-off", ip: "10.20.30.40", mask: "255.255.255.0");
        var agent = await AgentAsync("pc-v6", "10.20.30.77");
        await using (var db = _db.CreateDbContext())
        {
            var adapter = db.NetworkAdapters.Single(a => a.ComputerId == agent.Id);
            (adapter.IpAddresses, adapter.SubnetMasks) = ("fe80::1", "64");
            await db.SaveChangesAsync();
        }

        Assert.Equal(["pc-v6"], (await _wake.WakeAsync(off.Id))!.Relays);
    }

    [Fact]
    public async Task Only_agents_that_reported_recently_are_asked_and_at_most_two()
    {
        var off = await ComputerAsync("pc-off", ip: "10.20.30.40", mask: "255.255.255.0");
        await AgentAsync("pc-gone", "10.20.30.10", minutesAgo: 30);
        await AgentAsync("pc-a", "10.20.30.11", minutesAgo: 2);
        await AgentAsync("pc-b", "10.20.30.12", minutesAgo: 1);
        await AgentAsync("pc-c", "10.20.30.13");

        var result = await _wake.WakeAsync(off.Id);
        await result!.Relayed;

        // Die zuletzt gemeldeten zuerst.
        Assert.Equal(["pc-c", "pc-b"], result!.Relays);
    }

    [Fact]
    public async Task One_agent_gets_all_addresses_of_its_subnet_in_one_order_and_never_wakes_itself()
    {
        var first = await ComputerAsync("pc-01", mac: "00:1A:2B:3C:4D:01", ip: "10.20.30.41", mask: "255.255.255.0");
        var second = await ComputerAsync("pc-02", mac: "00:1A:2B:3C:4D:02", ip: "10.20.30.42", mask: "255.255.255.0");
        var agent = await AgentAsync("pc-agent", "10.20.30.77");

        var results = await _wake.WakeAsync([first.Id, second.Id, agent.Id]);
        await results[0].Relayed;

        var (host, step) = Assert.Single(_agents.Orders);
        Assert.Equal("pc-agent", host);
        Assert.Equal("00:1A:2B:3C:4D:01,00:1A:2B:3C:4D:02", step.Script);
        Assert.Empty(results.Single(r => r.ComputerId == agent.Id).Relays);
    }

    [Fact]
    public async Task Without_a_way_from_the_server_an_agent_still_counts_as_sent()
    {
        var off = await ComputerAsync("pc-off", ip: "10.20.30.40", mask: "255.255.255.0");
        await AgentAsync("pc-near", "10.20.30.77");
        var wake = new WakeService(_db, new NoRoute(), [_agents]);

        var result = await wake.WakeAsync(off.Id);
        await result!.Relayed;

        Assert.Equal(0, result!.Packets);
        Assert.True(result.Sent);
        Assert.False((await new WakeService(_db, new NoRoute()).WakeAsync(off.Id))!.Sent);
    }

    private sealed class NoRoute : IWakeSender
    {
        public Task<int> SendAsync(byte[] packet, IReadOnlyCollection<IPAddress> addresses, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public void The_relay_reads_the_addresses_of_its_order_and_builds_the_same_packet()
    {
        var macs = Inventory.Runner.WakeSender.ParseMacs("00:1A:2B:3C:4D:5E, 00-1A-2B-3C-4D-5F,nonsense,");

        Assert.Equal(["00:1A:2B:3C:4D:5E", "00:1A:2B:3C:4D:5F"], macs.Select(WakeOnLan.Format));
        Assert.Equal(WakeOnLan.MagicPacket(macs[0]), Inventory.Runner.WakeSender.Packet(macs[0]));
        Assert.Equal(IPAddress.Parse("10.20.31.255"), Inventory.Runner.WakeSender.Broadcast(IPAddress.Parse("10.20.30.40"), IPAddress.Parse("255.255.252.0")));
        Assert.Throws<InvalidOperationException>(() => Inventory.Runner.WakeSender.Send("nonsense"));
    }
}
