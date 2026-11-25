using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>
/// Rechner, die der DNS nicht kennt: eine von Hand eingetragene Verbindungsadresse, sonst die zuletzt bekannte IP-Adresse,
/// unter der sich dann aber der gemeinte Rechner mit seinem Namen melden muss.
/// </summary>
public class ConnectAddressTests
{
    [Theory]
    [InlineData(" 10.1.2.3 ", "10.1.2.3")]
    [InlineData("srv-web01.dmz.example.", "srv-web01.dmz.example")]
    [InlineData("srv-web01", "srv-web01")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_connection_address_is_an_ip_address_or_a_host_name(string? input, string? expected) =>
        Assert.Equal(expected, new ComputerService(new TestDb(), new SimulatedHostProbe()).NormalizeConnectAddress(input));

    [Theory]
    [InlineData("10.1")]
    [InlineData("10.1.2.300")]
    [InlineData("srv web01")]
    [InlineData("srv_web01/x")]
    [InlineData("-srv")]
    public void Anything_else_is_refused(string input) =>
        Assert.Throws<InvalidOperationException>(() => new ComputerService(new TestDb(), new SimulatedHostProbe()).NormalizeConnectAddress(input));

    [Fact]
    public void The_known_ip_is_a_fallback_only_for_a_name_without_a_fixed_address()
    {
        Assert.Equal("10.0.0.5", new Computer { Name = "pc-01", IpAddress = "10.0.0.5" }.FallbackAddress);
        Assert.Equal("pc-01.corp.example", new Computer { Name = "pc-01", IpAddress = "10.0.0.5", DnsName = "pc-01.corp.example" }.Address);

        var fixedAddress = new Computer { Name = "pc-01", IpAddress = "10.0.0.5", ConnectAddress = "192.168.50.11" };
        Assert.Equal(("192.168.50.11", null), (fixedAddress.Address, fixedAddress.FallbackAddress));
        Assert.Null(new Computer { Name = "10.0.0.9", IpAddress = "10.0.0.5" }.FallbackAddress);
        Assert.Null(new Computer { Name = "pc-01" }.FallbackAddress);
        Assert.Null(new Computer { Name = "mac-01", IpAddress = "10.0.0.5", Platform = OsPlatform.MacOS }.FallbackAddress);
    }

    /// <summary>pc-01 steht nicht im DNS; unter 10.0.0.5 meldet sich der Rechner, der in <paramref name="answeringName"/> steht.</summary>
    private static DnsProbe Probe(string? answeringName) => new(host => host switch
    {
        "10.0.0.5" when answeringName is not null => new ProbeResult(true, host, null, answeringName),
        "10.0.0.5" => new ProbeResult(false, host, "Ports unreachable"),
        "pc-01" => new ProbeResult(false, null, "Name resolution failed: HostNotFound", NameNotFound: true),
        "192.168.50.11" => new ProbeResult(true, host, null, "PC-01"),
        _ => new ProbeResult(true, "10.9.9.9", null, host.ToUpperInvariant()),
    });

    [Theory]
    [InlineData("PC-01", true)]
    [InlineData("PC-99", false)]
    [InlineData(null, false)]
    public async Task Only_the_meant_computer_counts_as_reachable_under_its_known_ip(string? answeringName, bool online)
    {
        var outcome = await Probe(answeringName).ProbeComputerAsync(new Computer { Name = "pc-01", IpAddress = "10.0.0.5" });

        Assert.Equal((true, true, online), (outcome.NameNotFound, outcome.ViaKnownIp, outcome.Result.Online));
    }

    [Fact]
    public async Task The_reachability_check_uses_the_known_ip_and_remembers_that_the_dns_lacks_the_name()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, Probe("PC-01"));
        var computer = (await computers.ImportAsync("pc-01"))[0];
        await using (var context = db.CreateDbContext())
            await context.Computers.Where(c => c.Id == computer.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.IpAddress, "10.0.0.5"));

        Assert.Equal(1, await computers.ProbeAsync([computer.Id]));
        var probed = (await computers.GetAsync(computer.Id))!;
        Assert.Equal((true, true, "PC-01"), (probed.IsOnline, probed.NameNotInDns, probed.ReportedName));

        // Eine eingetragene Verbindungsadresse geht vor; was die Prüfung über den Namen wusste, gilt dafür nicht.
        await computers.SetConnectAddressAsync(computer.Id, "192.168.50.11");
        Assert.False((await computers.GetAsync(computer.Id))!.NameNotInDns);
        await computers.ProbeAsync([computer.Id]);
        Assert.Equal((true, false), ((await computers.GetAsync(computer.Id))!.IsOnline, (await computers.GetAsync(computer.Id))!.NameNotInDns));
    }

    [Fact]
    public async Task Jobs_go_to_the_known_ip_only_with_a_name_check_even_for_a_scan()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, Probe("PC-01"));
        var smb = new Recorder();
        var jobs = new RemoteJobService([smb], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db), probe: Probe("PC-01"));
        var computer = (await computers.ImportAsync("pc-01\npc-02"))[0];
        await using (var context = db.CreateDbContext())
            await context.Computers.ExecuteUpdateAsync(s => s.SetProperty(c => c.IpAddress, "10.0.0.5"));
        JobManifest Scan() => new() { Steps = [new JobStep { Type = StepType.InventoryScan }] };

        Assert.True((await jobs.RunAsync(computer.Id, Scan())).Success);
        Assert.Equal(("10.0.0.5", "pc-01"), (smb.Target!.Host, smb.Job!.ExpectedComputerName));

        // Kennt der DNS den Namen, bleibt alles wie bisher: der Name, und für einen reinen Scan keine Namensprüfung.
        var known = (await computers.GetAsync(computer.Id + 1))!;
        Assert.True((await jobs.RunAsync(known.Id, Scan())).Success);
        Assert.Equal(("pc-02", null), (smb.Target!.Host, smb.Job!.ExpectedComputerName));

        // Eine eingetragene Verbindungsadresse gilt, auch wenn der DNS den Namen nicht kennt.
        await computers.SetConnectAddressAsync(computer.Id, "192.168.50.11");
        Assert.True((await jobs.RunAsync(computer.Id, Scan())).Success);
        Assert.Equal(("192.168.50.11", null), (smb.Target!.Host, smb.Job!.ExpectedComputerName));
    }

    [Fact]
    public async Task The_device_scan_gives_a_computer_without_an_address_the_one_where_it_answered_by_name()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var imported = await computers.ImportAsync("pc-01\npc-02");
        await using (var context = db.CreateDbContext())
            await context.Computers.Where(c => c.Name == "pc-02").ExecuteUpdateAsync(s => s.SetProperty(c => c.IpAddress, "10.0.0.20"));
        var devices = new DeviceService(db, new SimulatedDeviceProbe(), new ReversingProtector());
        var range = await devices.SaveRangeAsync(null, "Office", "10.0.0.0/24", true, null, null);

        await devices.StoreAsync(range.Id, [
            new DeviceObservation { IpAddress = "10.0.0.11", PingReply = true, WindowsName = "PC-01", OpenPorts = [445] },
            new DeviceObservation { IpAddress = "10.0.0.21", PingReply = true, WindowsName = "PC-02", OpenPorts = [445] },
        ], default);

        // pc-02 hatte schon eine Adresse aus dem eigenen Scan; die bleibt.
        Assert.Equal(["10.0.0.11", "10.0.0.20"], (await computers.ListAsync()).OrderBy(c => c.Name).Select(c => c.IpAddress));
    }

    private sealed class DnsProbe(Func<string, ProbeResult> result) : IHostProbe
    {
        public Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default) => Task.FromResult(result(host));
        public Task<bool> ResolvesAsync(string host, CancellationToken ct = default) => Task.FromResult(!result(host).NameNotFound);
    }

    private sealed class Recorder : IRemoteTransport
    {
        public TransportKind Kind => TransportKind.SmbService;
        public RemoteTarget? Target { get; private set; }
        public JobManifest? Job { get; private set; }

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            (Target, Job) = (target, job);
            return Task.FromResult(new JobResult { JobId = job.JobId, Success = true });
        }
    }
}
