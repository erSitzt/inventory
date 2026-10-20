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
    public void Names_may_contain_umlauts_and_other_letters()
    {
        Assert.Equal(["BÜRO-PC", "kasse-süd", "ÆRØ-01"], ComputerNames.Parse("BÜRO-PC\nkasse-süd\nÆRØ-01"));
        Assert.False(ComputerNames.IsValid("pc€01"));
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

    [Theory]
    [InlineData("inventory://assist/pc-01", "msra.exe", "/offerRA pc-01")]
    [InlineData("inventory://rdp/srv1.corp.local", "mstsc.exe", "/v:srv1.corp.local")]
    [InlineData("inventory://shadow/PC-01?session=3", "mstsc.exe", "/v:PC-01 /shadow:3 /control")]
    [InlineData("INVENTORY://ASSIST/pc-01/", "msra.exe", "/offerRA pc-01")]
    public void Launcher_translates_valid_links(string link, string file, string arguments)
    {
        Assert.True(Inventory.Launcher.LinkParser.TryParse(link, out var fileName, out var args, out _));
        Assert.Equal((file, arguments), (fileName, args));
    }

    [Theory]
    [InlineData("inventory://assist/pc-01%20/public")]          // eingeschleuster Parameter
    [InlineData("inventory://assist/pc-01 & calc")]
    [InlineData("inventory://assist/pc%2201")]
    [InlineData("inventory://assist/-pc")]
    [InlineData("inventory://assist/")]
    [InlineData("inventory://run/pc-01")]                       // unbekannte Aktion
    [InlineData("inventory://shadow/pc-01")]                    // Sitzung fehlt
    [InlineData("inventory://shadow/pc-01?session=1%20/prompt")]
    [InlineData("inventory://shadow/pc-01?session=-1")]
    [InlineData("https://assist/pc-01")]
    [InlineData("pc-01")]
    public void Launcher_rejects_everything_else(string link)
    {
        Assert.False(Inventory.Launcher.LinkParser.TryParse(link, out _, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Server_links_are_accepted_by_the_launcher()
    {
        foreach (var link in new[] { RemoteAccessLinks.LaunchAssistance("pc-01"), RemoteAccessLinks.LaunchRdp("srv1.corp.local"), RemoteAccessLinks.LaunchShadow("PC_01", 2) })
            Assert.True(Inventory.Launcher.LinkParser.TryParse(link, out _, out _, out _), link);
    }

    [Theory]
    [InlineData("pc-01", "pc-01.corp.example", "pc-01.corp.example")]
    [InlineData("pc-01", null, "pc-01")]
    [InlineData("pc-01", " ", "pc-01")]
    // Ein Eintrag, der selbst ein DNS-Name oder eine Adresse ist, gilt wie eingetragen.
    [InlineData("pc-01.other.example", "pc-01.corp.example", "pc-01.other.example")]
    [InlineData("10.1.2.3", "pc-01.corp.example", "10.1.2.3")]
    [InlineData("fe80::1", "pc-01.corp.example", "fe80::1")]
    // Ein DNS-Name, der zu einem anderen Rechner gehört (der Eintrag wurde umbenannt), wird nicht verwendet.
    [InlineData("pc-new", "pc-old.corp.example", "pc-new")]
    public void Connections_go_to_the_dns_name_if_one_is_known(string entered, string? dnsName, string expected) =>
        Assert.Equal(expected, Inventory.Core.Model.MachineNames.Address(entered, dnsName));

    [Theory]
    [InlineData("pc-01.corp.example", "PC-01", "pc-01.corp.example")]
    [InlineData("pc-01.corp.example.", "pc-01", "pc-01.corp.example")]
    // Längere DNS-Namen meldet der Rechner auf 15 Zeichen gekürzt.
    [InlineData("arbeitsplatz-buchhaltung.corp.example", "ARBEITSPLATZ-BU", "arbeitsplatz-buchhaltung.corp.example")]
    [InlineData("server.corp.example", "pc-01", null)]
    [InlineData("pc-01", "pc-01", null)]
    [InlineData("büro-pc.corp.example", "BÜRO-PC", "büro-pc.corp.example")]
    [InlineData("pc-01.corp example", "pc-01", null)]
    [InlineData("pc-01.-corp.example", "pc-01", null)]
    [InlineData("10.1.2.3", "10", null)]
    [InlineData(null, "pc-01", null)]
    public void A_dns_name_only_counts_if_it_belongs_to_the_computer(string? dnsName, string machine, string? expected) =>
        Assert.Equal(expected, Inventory.Core.Model.MachineNames.ValidDnsName(dnsName, machine));
}
