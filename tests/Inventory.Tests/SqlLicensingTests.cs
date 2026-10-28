using Inventory.Core.Licensing;

namespace Inventory.Tests;

/// <summary>SQL-Server-Berechnung und Rechenweg (Regeln aus rules/microsoft/sql-server.json).</summary>
public class SqlLicensingTests
{
    private static readonly MicrosoftRules Rules = MicrosoftRules.Current;
    private static readonly SqlServerOptions ListPrices = new(new Dictionary<string, decimal>());

    private static SqlInstanceInfo Instance(string edition, int version = 16, string name = "MSSQLSERVER") =>
        new(name, SqlServerEngine.ParseEdition(edition), version, false);

    private static SqlMachine Vm(int id, string name, string hostKey, int vcpus, params SqlInstanceInfo[] instances) =>
        new(id, name, true, hostKey, null, null, vcpus, instances);

    private static LicensingHost Host(string key, int sockets, int coresPerSocket) =>
        new(key, key, "PROD", "vCenter", sockets, coresPerSocket, sockets * coresPerSocket, false, null, null, []);

    [Theory]
    [InlineData("Enterprise Edition: Core-based Licensing (64-bit)", SqlEdition.Enterprise)]
    [InlineData("Enterprise Evaluation Edition (64-bit)", SqlEdition.Evaluation)]
    [InlineData("Standard Edition (64-bit)", SqlEdition.Standard)]
    [InlineData("Developer Edition (64-bit)", SqlEdition.Developer)]
    [InlineData("Express Edition with Advanced Services (64-bit)", SqlEdition.Express)]
    [InlineData("Web Edition (64-bit)", SqlEdition.Web)]
    [InlineData(null, SqlEdition.Unknown)]
    public void Editions_are_recognised(string? text, SqlEdition expected) => Assert.Equal(expected, SqlServerEngine.ParseEdition(text));

    [Fact]
    public void The_rules_file_is_embedded()
    {
        Assert.Equal((4, 4, 16), (Rules.SqlServer.MinCoresPerProcessor, Rules.SqlServer.MinCoresPerVm, Rules.SqlServer.PerVmRequiresSoftwareAssuranceFromMajorVersion));
        Assert.True(Rules.Price(MicrosoftRules.SqlEnterprise, new Dictionary<string, decimal>()) > Rules.Price(MicrosoftRules.SqlStandard, new Dictionary<string, decimal>()));
    }

    [Fact]
    public void A_physical_server_licenses_all_cores_with_at_least_four_per_processor_in_its_highest_edition()
    {
        var server = new SqlMachine(1, "sql-phys", false, null, 2, 2, null, [Instance("Standard Edition (64-bit)"), Instance("Enterprise Edition (64-bit)", name: "BI")]);
        var result = SqlServerEngine.Calculate([server], new Dictionary<string, LicensingHost>(), ListPrices, Rules);
        var entry = Assert.Single(result.Entries);
        Assert.Equal((MicrosoftRules.SqlEnterprise, 8), (entry.Chosen!.Product, entry.Chosen.Cores));
        Assert.Equal(8, result.Need[MicrosoftRules.SqlEnterprise]);
        Assert.NotEmpty(entry.Steps);
    }

    [Fact]
    public void Small_vms_are_licensed_per_vm_with_sa_and_many_vms_make_the_whole_host_cheaper()
    {
        var hosts = new Dictionary<string, LicensingHost> { ["h1"] = Host("h1", 2, 16) };
        var withSa = ListPrices with { SoftwareAssurance = true };
        // Eine VM mit 2 vCPU: 4 Kerne Standard je VM statt 32 Kerne Enterprise.
        var single = SqlServerEngine.Calculate([Vm(1, "sql-a", "h1", 2, Instance("Standard Edition (64-bit)"))], hosts, withSa, Rules);
        Assert.Equal((MicrosoftRules.SqlStandard, 4), (single.Entries[0].Chosen!.Product, single.Entries[0].Chosen!.Cores));
        // Zehn Enterprise-VMs mit je 4 vCPU (40 Kerne) gegen den ganzen Host (32 Kerne): der Host lohnt sich.
        var many = Enumerable.Range(1, 10).Select(i => Vm(i, $"sql-{i}", "h1", 4, Instance("Enterprise Edition (64-bit)"))).ToArray();
        var whole = Assert.Single(SqlServerEngine.Calculate(many, hosts, withSa, Rules).Entries);
        Assert.Equal((MicrosoftRules.SqlEnterprise, 32), (whole.Chosen!.Product, whole.Chosen.Cores));
        Assert.Equal(10, whole.WindowsVms);
    }

    [Fact]
    public void Sql_2022_per_vm_needs_sa_and_without_it_the_host_covers_as_many_vms_as_core_licences()
    {
        var hosts = new Dictionary<string, LicensingHost> { ["h1"] = Host("h1", 1, 8) };
        var vms = new[] { Vm(1, "sql-a", "h1", 4, Instance("Standard Edition (64-bit)", 16)) };
        var entry = Assert.Single(SqlServerEngine.Calculate(vms, hosts, ListPrices, Rules).Entries);
        Assert.False(entry.Variants[0].Allowed);
        // Ohne SA: alle 8 Kerne des Hosts in der Edition der VM (Standard) decken bis zu 8 VMs.
        Assert.Equal((MicrosoftRules.SqlStandard, 8), (entry.Chosen!.Product, entry.Chosen.Cores));
        var crowded = Enumerable.Range(1, 9).Select(i => Vm(i, $"sql-{i}", "h1", 2, Instance("Standard Edition (64-bit)", 16))).ToArray();
        Assert.Null(Assert.Single(SqlServerEngine.Calculate(crowded, hosts, ListPrices, Rules).Entries).Chosen);
        // SQL Server 2019 darf ohne SA je VM lizenziert werden.
        var older = Assert.Single(SqlServerEngine.Calculate([Vm(1, "sql-a", "h1", 4, Instance("Standard Edition (64-bit)", 15))], hosts, ListPrices, Rules).Entries);
        Assert.Equal((MicrosoftRules.SqlStandard, 4), (older.Chosen!.Product, older.Chosen.Cores));
    }

    [Fact]
    public void Free_editions_cost_nothing_and_vms_without_host_are_licensed_per_vm()
    {
        var machines = new[]
        {
            new SqlMachine(1, "dev01", false, null, 1, 8, null, [Instance("Developer Edition (64-bit)")]),
            Vm(2, "sql-lost", "unknown-host", 6, Instance("Standard Edition (64-bit)", 15)),
        };
        var result = SqlServerEngine.Calculate(machines, new Dictionary<string, LicensingHost>(), ListPrices, Rules);
        Assert.Single(result.Free);
        Assert.Contains("production", result.Free[0].Text);
        var lost = Assert.Single(result.Entries);
        Assert.Equal((MicrosoftRules.SqlStandard, 6, "VM"), (lost.Chosen!.Product, lost.Chosen.Cores, lost.Host.Source));
    }

    [Theory]
    [InlineData(147, 846, 11)]
    [InlineData(100, 150, 3)]
    [InlineData(0, 846, null)]
    public void Datacenter_break_even_follows_the_prices(int standard, int datacenter, int? vms) =>
        Assert.Equal(vms, WindowsServerEngine.BreakEvenVms(standard, datacenter, 2));

    [Fact]
    public void Every_host_shows_its_calculation_steps()
    {
        var host = new LicensingHost("h", "esx", null, "vCenter", 2, 6, 12, false, null, null, [new("a", 4, true, true, null), new("b", 4, true, true, null), new("c", 4, true, true, null)]);
        var steps = Assert.Single(WindowsServerEngine.Calculate([host], new WindowsServerOptions(new Dictionary<string, decimal>()), Rules).Hosts).Steps;
        Assert.Contains(steps, s => s.Text.StartsWith("{0} processors") && s.Args[3] is 16);
        Assert.Contains(steps, s => s.Text.StartsWith("Standard:") && s.Args[3] is 2 && s.Args[4] is 32);
        Assert.Contains(steps, s => s.Text.StartsWith("At these prices Datacenter pays off") && s.Args[0] is 11);
    }

    [Fact]
    public void In_a_cluster_whose_vms_move_licences_follow_the_vm_only_with_sa()
    {
        var hosts = new Dictionary<string, LicensingHost> { ["h1"] = Host("h1", 2, 16), ["h2"] = Host("h2", 2, 16) };
        var vms = new[] { Vm(1, "sql-a", "h1", 4, Instance("Standard Edition (64-bit)", 15)) };
        // Ohne SA bleibt die Lizenz 90 Tage auf ihrem Host: beide Hosts brauchen Lizenzen für die VM, die zu ihnen wandern kann.
        var without = SqlServerEngine.Calculate(vms, hosts, ListPrices, Rules).Entries;
        Assert.Equal(2, without.Count);
        Assert.All(without, e => Assert.Equal((MicrosoftRules.SqlStandard, 4, 1), (e.Chosen!.Product, e.Chosen.Cores, e.WindowsVms)));
        // Mit SA wandert sie mit (License Mobility): einmal 4 Kerne, der zweite Host braucht nichts zusätzlich.
        var with = SqlServerEngine.Calculate(vms, hosts, ListPrices with { SoftwareAssurance = true }, Rules).Entries;
        Assert.Equal([4, 0], with.OrderBy(e => e.Host.Key).Select(e => e.Chosen!.Cores));
        Assert.All(with, e => Assert.Equal(LicenseVariant.PlanVm, e.Chosen!.Plan));
        // Bleiben die VMs auf ihrem Host, zählt nur der Host der VM.
        var fixedHosts = hosts.ToDictionary(h => h.Key, h => h.Value with { Mobile = false });
        Assert.Single(SqlServerEngine.Calculate(vms, fixedHosts, ListPrices, Rules).Entries);
    }

    [Fact]
    public void Per_vm_licensing_counts_enterprise_and_standard_vms_each_in_their_product()
    {
        var hosts = new Dictionary<string, LicensingHost> { ["h1"] = Host("h1", 2, 16) };
        var vms = new[] { Vm(1, "sql-ent", "h1", 4, Instance("Enterprise Edition (64-bit)")), Vm(2, "sql-std", "h1", 6, Instance("Standard Edition (64-bit)")) };
        var result = SqlServerEngine.Calculate(vms, hosts, ListPrices with { SoftwareAssurance = true }, Rules);
        var entry = Assert.Single(result.Entries);
        Assert.Equal([(MicrosoftRules.SqlEnterprise, 4), (MicrosoftRules.SqlStandard, 6)], entry.Chosen!.Parts.Select(p => (p.Product, p.Cores)).OrderBy(p => p.Product));
        Assert.Equal((4, 6), (result.Need[MicrosoftRules.SqlEnterprise], result.Need[MicrosoftRules.SqlStandard]));
        // Bestand an Standard deckt nur den Standard-Teil; zu kaufen bleibt die Enterprise-VM.
        var stock = new Dictionary<string, int> { [MicrosoftRules.SqlStandard] = 6 };
        var covered = Assert.Single(SqlServerEngine.Calculate(vms, hosts, ListPrices with { SoftwareAssurance = true, Stock = stock }, Rules).Entries);
        Assert.Equal(6, covered.FromStock);
        Assert.Equal(Rules.Price(MicrosoftRules.SqlEnterprise, new Dictionary<string, decimal>()) * 2, covered.AdditionalCost);
    }
}
