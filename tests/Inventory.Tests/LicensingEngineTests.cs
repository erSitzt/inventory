using Inventory.Core.Licensing;

namespace Inventory.Tests;

/// <summary>Referenzfälle der Windows-Server-Berechnung (Regeln aus rules/microsoft/windows-server.json).</summary>
public class LicensingEngineTests
{
    private static readonly MicrosoftRules Rules = MicrosoftRules.Current;
    private static readonly WindowsServerOptions ListPrices = new(new Dictionary<string, decimal>());

    private static LicensingVm Vm(string name, int vcpus = 4, bool running = true, bool? windows = true) => new(name, vcpus, running, windows, null);

    private static LicensingHost Esx(string name, int sockets, int coresPerSocket, string? cluster = null, params LicensingVm[] vms) =>
        new($"vcenter:{name}", name, cluster, "vCenter", sockets, coresPerSocket, sockets * coresPerSocket, false, null, null, vms);

    private static HostLicensing Single(LicensingHost host, WindowsServerOptions? options = null) =>
        Assert.Single(WindowsServerEngine.Calculate([host], options ?? ListPrices, Rules).Hosts);

    [Fact]
    public void The_rules_file_is_embedded_and_valid()
    {
        Assert.False(string.IsNullOrWhiteSpace(Rules.Version));
        Assert.Equal((8, 16, 2, 2), (Rules.WindowsServer.MinCoresPerProcessor, Rules.WindowsServer.MinCoresPerServer, Rules.WindowsServer.StandardVmsPerLicense, Rules.PackSize));
        Assert.Throws<InvalidDataException>(() => MicrosoftRules.Parse("""{"version":"x","packSize":2,"windowsServer":{"minCoresPerProcessor":8,"minCoresPerServer":4,"standardVmsPerLicense":2,"perVmMinCores":8},"products":[]}"""));
    }

    [Fact]
    public void A_small_physical_server_is_licensed_with_the_minimum_of_sixteen_cores()
    {
        // 1 CPU mit 4 Kernen: 8 je Prozessor, 16 je Server.
        var result = Single(new LicensingHost("c:1", "srv-small", null, "physical", 1, 4, 4, true, "ServerStandard", 1, []));
        Assert.Equal(16, result.LicensedCores);
        Assert.Equal((MicrosoftRules.WindowsServerStandard, 16), (result.Chosen!.Product, result.Chosen.Cores));
    }

    [Fact]
    public void Hyperthreading_does_not_count_and_processors_below_eight_cores_are_raised()
    {
        // 2 × 6 Kerne (24 Threads): 2 × 8 = 16.
        Assert.Equal(16, Single(new LicensingHost("c:2", "srv-ht", null, "physical", 2, 6, 12, true, "ServerStandard", 2, [])).LicensedCores);
        // 2 × 24 Kerne: 48.
        Assert.Equal(48, Single(Esx("esx", 2, 24, null, Vm("a"))).LicensedCores);
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(2, 32)]
    [InlineData(3, 64)]
    [InlineData(4, 64)]
    [InlineData(5, 96)]
    public void Standard_stacks_all_cores_for_every_two_vms(int vms, int standardCores)
    {
        var host = Esx("esx", 2, 16, null, Enumerable.Range(1, vms).Select(i => Vm($"vm{i}")).ToArray());
        Assert.Equal(standardCores, Single(host).Variants.Single(v => v.Product == MicrosoftRules.WindowsServerStandard).Cores);
    }

    [Fact]
    public void A_cluster_is_licensed_for_all_its_windows_vms_on_every_host_and_datacenter_wins_at_high_density()
    {
        // 2 × 24 Kerne je Host, zwei Hosts, 30 VMs im Cluster: Standard bräuchte 15 × 48 Kerne, Datacenter 48.
        var vms = Enumerable.Range(1, 30).Select(i => Vm($"vm{i}")).ToList();
        var hosts = new[] { Esx("esx01", 2, 24, "PROD", vms.Take(15).ToArray()), Esx("esx02", 2, 24, "PROD", vms.Skip(15).ToArray()) };
        var result = WindowsServerEngine.Calculate(hosts, ListPrices, Rules);
        Assert.All(result.Hosts, h =>
        {
            Assert.Equal(30, h.WindowsVms);
            Assert.Equal((MicrosoftRules.WindowsServerDatacenter, 48), (h.Chosen!.Product, h.Chosen.Cores));
            Assert.Equal(48 * 15, h.Variants.Single(v => v.Product == MicrosoftRules.WindowsServerStandard).Cores);
        });
        Assert.Equal(96, result.Need[MicrosoftRules.WindowsServerDatacenter]);

        // Ohne Cluster-Betrachtung zählt jeder Host nur seine eigenen VMs.
        var own = WindowsServerEngine.Calculate(hosts, ListPrices with { ClusterWide = false }, Rules);
        Assert.All(own.Hosts, h => Assert.Equal(15, h.WindowsVms));
    }

    [Fact]
    public void Per_vm_licensing_needs_software_assurance_and_counts_at_least_eight_cores_per_vm()
    {
        // Eine kleine VM (2 vCPU) auf einem großen Host.
        var host = Esx("esx-big", 2, 32, null, Vm("small", vcpus: 2));
        var without = Single(host).Variants.Single(v => v.Product == MicrosoftRules.WindowsServerPerVm);
        Assert.Equal((8, false), (without.Cores, without.Allowed));
        var with = Single(host, ListPrices with { SoftwareAssurance = true });
        Assert.Equal((MicrosoftRules.WindowsServerPerVm, 8), (with.Chosen!.Product, with.Chosen.Cores));
    }

    [Fact]
    public void Hosts_without_windows_load_need_nothing_and_unknown_cores_are_reported_not_guessed()
    {
        var linuxOnly = Single(Esx("esx-linux", 2, 16, null, Vm("appliance", windows: false), Vm("off", running: false)));
        Assert.Null(linuxOnly.Chosen);
        var unknown = WindowsServerEngine.Calculate([new LicensingHost("v:9", "esx-unknown", null, "vCenter", null, null, null, false, null, null, [Vm("a")])], ListPrices, Rules);
        Assert.Null(Assert.Single(unknown.Hosts).Chosen);
        Assert.Contains(unknown.Gaps, g => g.Args.Contains("esx-unknown"));
        // Unbekanntes Betriebssystem zählt vorsichtshalber mit, mit Hinweis.
        var unsure = Single(Esx("esx", 1, 16, null, Vm("x", windows: null)));
        Assert.Equal(1, unsure.WindowsVms);
        Assert.NotEmpty(unsure.Notes);
    }

    [Fact]
    public void An_installed_datacenter_edition_rules_out_standard_and_own_prices_decide()
    {
        var hyperV = new LicensingHost("c:3", "hv-01", null, "Hyper-V", 2, 16, 32, true, "ServerDatacenter", 3, [Vm("a"), Vm("b")]);
        var result = Single(hyperV);
        Assert.False(result.Variants.Single(v => v.Product == MicrosoftRules.WindowsServerStandard).Allowed);
        Assert.Equal(MicrosoftRules.WindowsServerDatacenter, result.Chosen!.Product);
        // Mit eigenen Preisen, bei denen Datacenter billiger ist als zweimal Standard.
        var cheapDatacenter = new WindowsServerOptions(new Dictionary<string, decimal> { [MicrosoftRules.WindowsServerStandard] = 100, [MicrosoftRules.WindowsServerDatacenter] = 150 });
        Assert.Equal(MicrosoftRules.WindowsServerDatacenter, Single(Esx("esx", 2, 16, null, Vm("a"), Vm("b"), Vm("c")), cheapDatacenter).Chosen!.Product);
    }

    [Fact]
    public void Existing_stock_is_used_first_before_buying_the_cheapest_variant()
    {
        // Mit SA wäre je VM am günstigsten (3 × 8 Kerne); vorhandene Datacenter-Kerne kosten aber nichts mehr.
        var hosts = new[] { Esx("esx01", 2, 16, null, Vm("a"), Vm("b"), Vm("c")), Esx("esx02", 2, 16, null, Vm("d")) };
        var stock = new Dictionary<string, int> { [MicrosoftRules.WindowsServerDatacenter] = 32 };
        var result = WindowsServerEngine.Calculate(hosts, ListPrices with { SoftwareAssurance = true, Stock = stock }, Rules);
        var first = result.Hosts.Single(h => h.Host.Name == "esx01");
        Assert.Equal(MicrosoftRules.WindowsServerDatacenter, first.Chosen!.Product);
        // Begründung: vom Bestand gedeckt, die nächstbeste Variante (je VM) mit ihrem Betrag.
        var reason = Assert.Single(first.Decision);
        Assert.StartsWith("{0} is covered completely by the existing stock", reason.Text);
        Assert.Equal([MicrosoftRules.WindowsServerDatacenter, 32, MicrosoftRules.WindowsServerPerVm], reason.Args.Take(3));
        Assert.Equal(0, first.AdditionalCost);
        Assert.All(first.Variants, v => Assert.NotNull(v.Formula));
        // Der Bestand ist verbraucht: der zweite Host nimmt wieder die günstigste Variante.
        Assert.Equal((MicrosoftRules.WindowsServerPerVm, 8), (result.Hosts.Single(h => h.Host.Name == "esx02").Chosen!.Product, result.Hosts.Single(h => h.Host.Name == "esx02").Chosen!.Cores));
    }

    [Fact]
    public void Hosts_without_a_choice_get_the_matching_stock_first()
    {
        // "a-esx" käme alphabetisch zuerst und nähme sonst die Datacenter-Kerne, die der Hyper-V-Host mit installiertem Datacenter braucht.
        var hosts = new[]
        {
            Esx("a-esx", 2, 16, null, Vm("x"), Vm("y"), Vm("z")),
            new LicensingHost("c:1", "z-hyperv", null, "Hyper-V", 2, 16, 32, true, "ServerDatacenter", 1, [Vm("v")]),
        };
        var stock = new Dictionary<string, int> { [MicrosoftRules.WindowsServerDatacenter] = 32 };
        var result = WindowsServerEngine.Calculate(hosts, ListPrices with { SoftwareAssurance = true, Stock = stock }, Rules);
        var hyperV = result.Hosts.Single(h => h.Host.Name == "z-hyperv");
        Assert.Equal((32, "{0} is the only allowed variant."), (hyperV.FromStock, Assert.Single(hyperV.Decision).Text));
        Assert.Equal(MicrosoftRules.WindowsServerPerVm, result.Hosts.Single(h => h.Host.Name == "a-esx").Chosen!.Product);
    }

    [Fact]
    public void A_cluster_whose_vms_move_is_licensed_one_way_on_all_hosts()
    {
        // Zwei Hosts, 2 + 5 Windows-VMs; Datacenter-Bestand reicht nur für einen Host. Je Host gewählt bekäme der eine Datacenter aus dem
        // Bestand, der andere je VM – eine VM, die vom ersten auf den zweiten zieht, wäre dort nicht gedeckt.
        var hosts = new[]
        {
            Esx("xcp01", 2, 20, "POOL", Vm("a", 2), Vm("b", 2)),
            Esx("xcp02", 2, 20, "POOL", Vm("c", 2), Vm("d", 4), Vm("e", 2), Vm("f", 2), Vm("g", 2)),
        };
        var stock = new Dictionary<string, int> { [MicrosoftRules.WindowsServerDatacenter] = 40 };
        var result = WindowsServerEngine.Calculate(hosts, ListPrices with { SoftwareAssurance = true, Stock = stock }, Rules);
        Assert.All(result.Hosts, h =>
        {
            Assert.Equal(MicrosoftRules.WindowsServerPerVm, h.Chosen!.Product);
            Assert.Equal(7, h.WindowsVms);
            Assert.True(h.Mobile);
            Assert.StartsWith("All hosts of cluster {0} are licensed per VM", h.Decision[0].Text);
        });
        // Je VM zählt jede VM einmal, mindestens 8 Kerne: 2 × 8 und 5 × 8.
        Assert.Equal([16, 40], result.Hosts.Select(h => h.Chosen!.Cores));
        Assert.Equal([2, 5], result.Hosts.Select(h => h.VmsOnHost));

        // Ohne SA bleibt nur die Lizenzierung nach Kernen: beide Hosts für alle 7 VMs, der Bestand deckt einen davon.
        var withoutSa = WindowsServerEngine.Calculate(hosts, ListPrices with { Stock = stock }, Rules);
        Assert.All(withoutSa.Hosts, h => Assert.Equal(LicenseVariant.PlanHost, h.Chosen!.Plan));
        Assert.Equal(40, withoutSa.Hosts.Sum(h => h.FromStock));
        Assert.Contains(withoutSa.Hosts, h => h.AdditionalCost == 0);
    }

    [Fact]
    public void Vms_that_stay_on_their_host_count_only_there_and_hosts_may_differ()
    {
        var hosts = new[]
        {
            Esx("esx01", 2, 16, "LAB", Vm("a"), Vm("b")) with { Mobile = false, MobilitySource = MobilitySource.Setting },
            Esx("esx02", 2, 16, "LAB") with { Mobile = false, MobilitySource = MobilitySource.Setting },
        };
        var result = WindowsServerEngine.Calculate(hosts, ListPrices with { SoftwareAssurance = true }, Rules);
        var first = result.Hosts.Single(h => h.Host.Name == "esx01");
        Assert.Equal((2, false), (first.WindowsVms, first.Mobile));
        Assert.Contains(first.Notes, n => n.Text.StartsWith("Cluster {0} is set to: VMs stay on their host", StringComparison.Ordinal));
        // Ohne eigene VMs braucht der zweite Host nichts.
        Assert.Null(result.Hosts.Single(h => h.Host.Name == "esx02").Chosen);

        // Wandern die VMs (DRS), braucht auch der leere Host Lizenzen für beide.
        var drs = WindowsServerEngine.Calculate(hosts.Select(h => h with { Mobile = true, MobilitySource = MobilitySource.Drs }).ToList(), ListPrices, Rules);
        var empty = drs.Hosts.Single(h => h.Host.Name == "esx02");
        Assert.Equal((2, 0), (empty.WindowsVms, empty.VmsOnHost));
        Assert.Equal((MicrosoftRules.WindowsServerStandard, 32), (empty.Chosen!.Product, empty.Chosen.Cores));
        Assert.Contains(empty.Notes, n => n.Text.StartsWith("vSphere DRS is on", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_name_the_microsoft_documents()
    {
        Assert.Contains(Rules.Documents, d => d.Url.StartsWith("https://", StringComparison.Ordinal) && d.Title.Contains("Windows Server", StringComparison.Ordinal));
        Assert.Contains(Rules.SqlServer.Documents, d => d.Url.EndsWith(".pdf", StringComparison.Ordinal));
    }
}
