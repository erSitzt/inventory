using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Licensing;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class LicensingServiceTests
{
    private static LicensingService Service(TestDb db) => new(db, new SettingsService(db), new VirtualizationService(db), TimeProvider.System);

    [Fact]
    public async Task Stock_is_validated_and_imported_from_a_table_with_preview()
    {
        using var db = new TestDb();
        var service = Service(db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveEntitlementAsync(new LicenseEntitlement { Product = "Nonsense", Quantity = 2 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveEntitlementAsync(new LicenseEntitlement { Product = MicrosoftRules.WindowsServerDatacenter, Quantity = 0 }));

        const string csv = "Product;Quantity;Version;SA until;Contract;Notes\n"
                           + "windowsserverdatacentercore;96;2022;2027-06-30;EA-1;\n"
                           + "Windows Server user CAL;250;2022;;EA-1;\n"
                           + "Office;5;;;;\n"
                           + "WindowsServerStandardCore;x;;;;\n";
        var preview = await service.ImportAsync(csv, apply: false);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal(2, preview.Errors.Count);
        Assert.Empty(await service.EntitlementsAsync());
        await service.ImportAsync(csv, apply: true);
        var stock = await service.EntitlementsAsync();
        Assert.Equal([(MicrosoftRules.WindowsServerDatacenter, 96), ("WindowsServerUserCal", 250)], stock.Select(e => (e.Product, e.Quantity)).OrderBy(e => e.Product));
        Assert.Equal(new DateOnly(2027, 6, 30), stock.Single(e => e.Product == MicrosoftRules.WindowsServerDatacenter).SoftwareAssuranceUntil);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync("Name;Count\nx;1", apply: false));
    }

    [Fact]
    public async Task The_calculation_uses_hyper_v_and_vcenter_hosts_and_reports_vms_without_a_host()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        // Die Simulation: Hyper-V-Cluster mit HV-01/HV-02, Server-VMs darauf, eine VMware-VM ohne vCenter und ein physischer Server.
        var names = new[] { "hv-01", "hv-02", "srv-sql01", "srv-app01", "vm-sql01", "pc-01" };
        var ids = (await computers.ImportAsync(string.Join("\n", names))).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            await store.SaveAsync(ids[name], SimulatedInventory.Create(name));

        var service = Service(db);
        var overview = await service.CalculateAsync();
        var hyperV = overview.WindowsServer.Hosts.Where(h => h.Host.Source == "Hyper-V").ToList();
        Assert.Equal(2, hyperV.Count);
        // Datacenter ist installiert: Standard ist ausgeschlossen, 2 × 16 Kerne.
        Assert.All(hyperV, h => Assert.Equal((MicrosoftRules.WindowsServerDatacenter, 32), (h.Chosen!.Product, h.Chosen.Cores)));
        Assert.Equal(64, overview.Balances.Single(b => b.Product.Key == MicrosoftRules.WindowsServerDatacenter).Need);
        // Die VMware-VM kennt keinen Host.
        Assert.Contains(overview.Gaps, g => g.Args.Any(a => string.Equals(a as string, "vm-sql01", StringComparison.OrdinalIgnoreCase)));
        // Clients zählen nicht als Server.
        Assert.DoesNotContain(overview.WindowsServer.Hosts, h => h.Host.ComputerId == ids["pc-01"]);

        // Mit Bestand: Differenz und Software Assurance.
        await service.SaveEntitlementAsync(new LicenseEntitlement { Product = MicrosoftRules.WindowsServerDatacenter, Quantity = 48, SoftwareAssuranceUntil = DateOnly.FromDateTime(DateTime.Today.AddYears(1)) });
        overview = await service.CalculateAsync();
        var balance = overview.Balances.Single(b => b.Product.Key == MicrosoftRules.WindowsServerDatacenter);
        Assert.Equal((64, 48, -16, true), (balance.Need!.Value, balance.Stock, balance.Delta!.Value, overview.SoftwareAssurance));
        Assert.Equal("shortfall", service.Status(balance));
        Assert.Equal(["product", "unit", "need", "stock", "delta", "status", "needCost", "shortfallCost"], service.BalanceTable(overview).Columns.Select(c => c.Key));
        // SQL Server: SRV-SQL01 (Standard 2022) auf dem Hyper-V-Host ohne SA → ganzer Host in Standard; die VMware-VM ohne Host ist eine Lücke.
        var sql = Assert.Single(overview.SqlServer.Entries, e => e.Chosen is not null);
        Assert.Equal((MicrosoftRules.SqlStandard, 32), (sql.Chosen!.Product, sql.Chosen.Cores));
        Assert.Contains(overview.Gaps, g => g.Text.Contains("SQL Server 2022 or later on a VM without a known host"));
        // Im Bericht stehen auch die nicht berechenbaren Einträge (die VMware-VM ohne Host).
        Assert.Equal(4, service.HostTable(overview).Rows.Count);
    }

    [Fact]
    public async Task Own_prices_and_the_cluster_option_are_stored()
    {
        using var db = new TestDb();
        var service = Service(db);
        Assert.True((await service.GetSettingsAsync()).ClusterWide);
        await service.SaveSettingsAsync(new Dictionary<string, decimal?> { [MicrosoftRules.WindowsServerStandard] = 120m, ["Unknown"] = 5m, [MicrosoftRules.WindowsServerDatacenter] = null }, false);
        var settings = await service.GetSettingsAsync();
        Assert.False(settings.ClusterWide);
        Assert.Equal(new Dictionary<string, decimal> { [MicrosoftRules.WindowsServerStandard] = 120m }, settings.Prices);
    }

    [Fact]
    public async Task The_report_has_a_summary_with_totals_and_the_calculation_per_host()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var names = new[] { "hv-01", "hv-02", "srv-sql01", "srv-sql02" };
        var ids = (await computers.ImportAsync(string.Join("\n", names))).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            await store.SaveAsync(ids[name], SimulatedInventory.Create(name));
        var service = Service(db);
        await service.SaveEntitlementAsync(new LicenseEntitlement { Product = MicrosoftRules.WindowsServerDatacenter, Quantity = 16, ContractNumber = "EA-1" });
        var overview = await service.CalculateAsync();

        var balance = overview.Balances.Single(b => b.Product.Key == MicrosoftRules.WindowsServerDatacenter);
        Assert.Equal(64 * 846m / 2, balance.NeedCost);
        Assert.Equal(48 * 846m / 2, balance.ShortfallCost);
        var table = service.BalanceTable(overview, total: true);
        Assert.Equal("Total", table.Rows[^1][0]);

        var sections = service.ReportSections(overview, await service.EntitlementsAsync());
        Assert.Contains(sections, s => s.Heading == "Windows Server: hv-01 (HVCLUSTER01)" && s.Paragraphs!.Any(p => p.StartsWith("1. 2 processors")));
        Assert.Contains(sections, s => s.Heading.StartsWith("SQL Server: "));
        var pdf = ReportPdf.WriteSections(sections, new ReportPdfOptions("Microsoft licensing", "test"));
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages >= 3);
        var text = string.Join("\n", document.GetPages().Select(p => p.Text));
        Assert.Contains("Summary", text);
        Assert.Contains("Total", text);
        Assert.Contains("Rules and assumptions", text);
        Assert.Contains("EA-1", text);
    }

    [Fact]
    public void A_long_report_table_keeps_its_total_row()
    {
        var rows = Enumerable.Range(1, ReportPdf.MaxRows + 10).Select(i => new string?[] { $"row {i}", "1" }).ToList();
        rows.Add(["TOTAL-ROW", "9999"]);
        var pdf = ReportPdf.WriteSections([new ReportSection("Long", Table: new ReportTable([new("a", "A"), new("b", "B", true)], rows), TotalRow: true)],
            new ReportPdfOptions("Long"));
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Contains("TOTAL-ROW", document.GetPage(document.NumberOfPages).Text);
        Assert.Contains($"First {ReportPdf.MaxRows} of {rows.Count} rows.", document.GetPage(1).Text);
    }
}
