using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class ComputerTests
{
    [Fact]
    public void Parse_splits_dedupes_and_drops_invalid_names()
    {
        var names = ComputerNames.Parse("pc-01\nPC-01; \\\\pc-02, srv1.corp.local\n\nbad/name  pc 03");

        Assert.Equal(["pc-01", "pc-02", "srv1.corp.local", "pc", "03"], names);
    }

    [Fact]
    public async Task Import_skips_existing_names_case_insensitively()
    {
        using var db = new TestDb();
        var service = new ComputerService(db, new SimulatedHostProbe());

        Assert.Equal(2, (await service.ImportAsync("PC-01\nPC-02")).Count);
        var added = await service.ImportAsync("pc-01\npc-03");

        Assert.Equal("pc-03", Assert.Single(added).Name);
        Assert.Equal(3, (await service.ListAsync()).Count);
    }

    [Fact]
    public async Task Probe_stores_status_and_keeps_last_seen_when_offline()
    {
        using var db = new TestDb();
        var online = true;
        var service = new ComputerService(db, new FixedProbe(_ => new ProbeResult(online, online ? "10.1.1.1" : null)));
        await service.ImportAsync("pc-01");

        Assert.Equal(1, await service.ProbeAsync());
        var seen = (await service.ListAsync())[0].LastSeenUtc;
        online = false;
        Assert.Equal(0, await service.ProbeAsync());

        var computer = (await service.ListAsync())[0];
        Assert.False(computer.IsOnline);
        Assert.Equal("10.1.1.1", computer.IpAddress);
        Assert.Equal(seen, computer.LastSeenUtc);
    }

    [Fact]
    public async Task Simulated_probe_is_stable_per_name()
    {
        var probe = new SimulatedHostProbe();

        Assert.Equal(await probe.ProbeAsync("PC-01"), await probe.ProbeAsync("pc-01"));
    }

    [Fact]
    public void Remote_access_commands_and_links_are_built_from_name_and_ids()
    {
        Assert.Equal("msra.exe /offerRA pc-01", RemoteAccessLinks.OfferAssistance("pc-01"));
        Assert.Equal("mstsc.exe /v:pc-01.corp.local /shadow:3 /control", RemoteAccessLinks.Shadow("pc-01.corp.local", 3));
        Assert.Equal("teamviewer10://control?device=123456789", RemoteAccessLinks.TeamViewer("123456789"));
        Assert.Equal("anydesk:123%20456", RemoteAccessLinks.AnyDesk("123 456"));
        Assert.StartsWith("full address:s:pc-01\r\n", RemoteAccessLinks.RdpFile("pc-01"));
    }
}
