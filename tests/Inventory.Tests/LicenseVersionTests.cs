using Inventory.Core.Licensing;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

/// <summary>Lizenzen decken ihre Version und ältere; mit SA auch jede Version, die bis zu ihrem Ende erschienen ist.</summary>
public class LicenseVersionTests
{
    private static readonly MicrosoftRules Rules = MicrosoftRules.Current;
    private static readonly WindowsServerOptions ListPrices = new(new Dictionary<string, decimal>());

    private static LicensingVm Vm(string name, int? version) => new(name, 4, true, true, null) { Version = version };

    private static LicensingHost Esx(string name, params LicensingVm[] vms) =>
        new($"vcenter:{name}", name, null, "vCenter", 2, 16, 32, false, null, null, vms);

    [Fact]
    public void Versions_are_read_from_the_build_or_the_name()
    {
        var windows = Rules.WindowsServer.Versions;
        Assert.Equal(2022, LicenseVersions.WindowsServer("Microsoft Windows Server 2022 Standard", "10.0.20348.2700", windows));
        Assert.Equal(2025, LicenseVersions.WindowsServer(null, "10.0.26100.1", windows));
        Assert.Equal(2016, LicenseVersions.WindowsServer("Microsoft Windows Server 2016 Datacenter", null, windows));
        // So meldet vSphere manche Gäste: keine Version.
        Assert.Null(LicenseVersions.WindowsServer("Microsoft Windows Server 2019 or later (64-bit)", null, windows));
        // Eine Jahreszahl nach der neuesten bekannten Version ist unbekannt.
        Assert.Null(LicenseVersions.WindowsServer("Windows Server 2031 Datacenter", null, windows));
        Assert.Null(LicenseVersions.Parse("2031", windows));
        var sql = Rules.SqlServer.Versions;
        Assert.Equal(2022, LicenseVersions.Sql(16, sql));
        Assert.Equal((2022, 2019, (int?)null), (LicenseVersions.Parse("Windows Server 2022", windows), LicenseVersions.Parse("15", sql), LicenseVersions.Parse("current", sql)));
    }

    [Fact]
    public void Software_assurance_adds_the_versions_released_while_it_ran()
    {
        var windows = Rules.WindowsServer.Versions;
        var today = new DateOnly(2026, 11, 3);
        Assert.Equal(2022, LicenseVersions.Covers(2022, null, today, windows));
        // SA bis Mitte 2025: Windows Server 2025 (November 2024) ist in der Laufzeit erschienen und bleibt.
        Assert.Equal(2025, LicenseVersions.Covers(2022, new DateOnly(2025, 6, 30), today, windows));
        Assert.Equal(2022, LicenseVersions.Covers(2022, new DateOnly(2024, 6, 30), today, windows));
        Assert.Null(LicenseVersions.Covers(null, new DateOnly(2027, 1, 1), today, windows));
    }

    [Fact]
    public void Stock_of_an_older_version_does_not_cover_a_newer_one()
    {
        var stock = new LicenseStock([(MicrosoftRules.WindowsServerDatacenter, 2022, 64), (MicrosoftRules.WindowsServerDatacenter, 2025, 16)]);
        Assert.Equal((80, 16, 80), (stock.Available(MicrosoftRules.WindowsServerDatacenter, 2019), stock.Available(MicrosoftRules.WindowsServerDatacenter, 2025),
            stock.Available(MicrosoftRules.WindowsServerDatacenter, null)));
        // Die kleinste passende Version zuerst: die 2025-Kerne bleiben für 2025.
        Assert.Equal(32, stock.Take(MicrosoftRules.WindowsServerDatacenter, 2019, 32));
        Assert.Equal(16, stock.Available(MicrosoftRules.WindowsServerDatacenter, 2025));
    }

    [Fact]
    public void A_host_with_a_newer_version_is_not_covered_by_older_stock()
    {
        var hosts = new[] { Esx("esx-new", Vm("a", 2025), Vm("b", 2022), Vm("c", 2022)), Esx("esx-old", Vm("d", 2022), Vm("e", 2019), Vm("f", 2022)) };
        // Drei VMs je Host: Standard zweimal über alle 32 Kerne.
        var stock = new LicenseStock([(MicrosoftRules.WindowsServerStandard, 2022, 64)]);
        var result = WindowsServerEngine.Calculate(hosts, ListPrices with { Stock = stock }, Rules);
        var older = result.Hosts.Single(h => h.Host.Name == "esx-old");
        Assert.Equal((MicrosoftRules.WindowsServerStandard, 2022, 64, 0m), (older.Chosen!.Product, older.Chosen.Version, older.FromStock, older.AdditionalCost));
        var newer = result.Hosts.Single(h => h.Host.Name == "esx-new");
        Assert.Equal((2025, 0), (newer.Chosen!.Version, newer.FromStock));
        Assert.True(newer.AdditionalCost > 0);
        Assert.Contains(newer.Decision, d => d.Text.StartsWith("{0} cores in stock are for older versions"));
        Assert.Contains(result.NeedByVersion, n => n.Version == 2025 && n.Cores > 0);
    }

    [Fact]
    public void Unknown_versions_count_as_the_newest_and_are_reported()
    {
        var result = WindowsServerEngine.Calculate([Esx("esx", Vm("mystery", null), Vm("known", 2019))], ListPrices, Rules);
        Assert.Equal(LicenseVersions.Newest(Rules.WindowsServer.Versions), Assert.Single(result.Hosts).Chosen!.Version);
        Assert.Contains(result.Gaps, g => g.Args.Contains("mystery"));
    }

    [Fact]
    public async Task The_balance_shows_what_is_missing_per_version_even_with_enough_cores()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var names = new[] { "hv-01", "hv-02", "srv-sql01", "srv-app01" };
        var ids = (await computers.ImportAsync(string.Join("\n", names))).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            await store.SaveAsync(ids[name], SimulatedInventory.Create(name));
        var service = new LicensingService(db, new SettingsService(db), new VirtualizationService(db), TimeProvider.System);
        var need = (await service.CalculateAsync()).Balances.Single(b => b.Product.Key == MicrosoftRules.WindowsServerDatacenter).Need!.Value;

        // Genug Kerne, aber nur für 2016: die Hosts laufen mit 2022.
        await service.SaveEntitlementAsync(new LicenseEntitlement { Product = MicrosoftRules.WindowsServerDatacenter, Quantity = need + 16, Version = "2016", ContractNumber = "EA-1" });
        var overview = await service.CalculateAsync();
        var balance = overview.Balances.Single(b => b.Product.Key == MicrosoftRules.WindowsServerDatacenter);
        Assert.True(balance.Delta > 0);
        Assert.Equal(need, balance.Missing);
        Assert.Equal("shortfall", service.Status(balance));
        Assert.Contains(balance.Versions, v => v.Version == 2016 && v.Stock == need + 16 && v.Need == 0);
        Assert.Equal(need, balance.Versions.Where(v => v.Version > 2016).Sum(v => v.Missing));
        Assert.Contains(service.BalanceTable(overview).Rows, r => r[^1] is { } text && text.Contains("2016"));
        // Woher die Zahlen kommen: die Hosts mit ihren Kernen, die Bestandseinträge mit Vertrag.
        var needSources = service.NeedSources(overview, MicrosoftRules.WindowsServerDatacenter);
        Assert.Contains(needSources, line => line.Contains("HV-01", StringComparison.OrdinalIgnoreCase) && line.Contains("cores, version 20"));
        Assert.Empty(service.NeedSources(overview, MicrosoftRules.WindowsServerDatacenter, balance.Versions.Single(v => v.Version == 2016)));
        // SQL Server: die VMs auf dem Host und die, die ohne SA aus dem Cluster hierher wandern können.
        Assert.Equal(["hv-01 · HVCLUSTER01 (srv-sql01): 32 cores, version 2022", "hv-02 · HVCLUSTER01 (may move here: srv-sql01): 32 cores, version 2022"],
            service.NeedSources(overview, MicrosoftRules.SqlStandard));
        Assert.Equal([$"EA-1: {need + 16} · Version 2016"], service.StockSources(await service.EntitlementsAsync(), MicrosoftRules.WindowsServerDatacenter));

        // Ohne Version zählt der Bestand wie bisher für jede, mit Hinweis.
        await service.SaveEntitlementAsync(new LicenseEntitlement { Product = MicrosoftRules.WindowsServerDatacenter, Quantity = need, ContractNumber = "EA-2" });
        overview = await service.CalculateAsync();
        Assert.Equal(0, overview.Balances.Single(b => b.Product.Key == MicrosoftRules.WindowsServerDatacenter).Missing);
        Assert.Contains(overview.Gaps, g => g.Text.Contains("no version entered") && g.Args.Contains("EA-2"));
    }

    [Fact]
    public void One_unknown_sql_instance_makes_the_machine_count_as_the_newest_version()
    {
        var vm = new SqlMachine(1, "vm-sql", true, null, null, null, 4,
            [new SqlInstanceInfo("A", SqlEdition.Standard, 15, false), new SqlInstanceInfo("B", SqlEdition.Standard, null, false)]);
        var result = SqlServerEngine.Calculate([vm], new Dictionary<string, LicensingHost>(), new SqlServerOptions(new Dictionary<string, decimal>()), Rules);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(LicenseVersions.Newest(Rules.SqlServer.Versions), entry.Variants[0].Version);
        // Als neueste Version braucht sie für die Lizenzierung je VM SA.
        Assert.False(entry.Variants[0].Allowed);
        Assert.Contains(result.Gaps, g => g.Text.Contains("SQL Server version is unknown") && g.Args.Contains("vm-sql"));
    }
}
