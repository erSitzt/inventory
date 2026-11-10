using Inventory.Agent.MacOS;
using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

/// <summary>Was der Mac-Agent über Programme, Pakete, Netzwerk und Benutzer hinaus liefert, und die macOS-Unterstützung je Modell.</summary>
public class MacInventoryTests
{
    [Fact]
    public void Apps_carry_origin_and_architecture()
    {
        using var json = MacParsers.Json("""
            {"SPApplicationsDataType":[
              {"_name":"Old Tool","path":"/Applications/Old Tool.app","arch_kind":"arch_i64","obtained_from":"unknown","version":"1.0"},
              {"_name":"Chrome","path":"/Applications/Chrome.app","arch_kind":"arch_arm_i64","obtained_from":"identified_developer","version":"130"},
              {"_name":"Pages","path":"/Applications/Pages.app","arch_kind":"arch_arm","obtained_from":"mac_app_store","version":"14"},
              {"_name":"Legacy","path":"/Applications/Legacy.app","arch_kind":"arch_i32","obtained_from":"identified_developer","version":"2"}
            ]}
            """);
        var apps = MacParsers.Applications(json).ToDictionary(a => a.Name);
        Assert.Equal((SoftwareArchitectures.Intel, SoftwareOrigins.Unknown), (apps["Old Tool"].Architecture, apps["Old Tool"].Origin));
        Assert.Equal((SoftwareArchitectures.Universal, SoftwareOrigins.IdentifiedDeveloper), (apps["Chrome"].Architecture, apps["Chrome"].Origin));
        Assert.Equal((SoftwareArchitectures.AppleSilicon, SoftwareOrigins.AppStore), (apps["Pages"].Architecture, apps["Pages"].Origin));
        Assert.Equal((SoftwareArchitectures.Intel, false), (apps["Legacy"].Architecture, apps["Legacy"].Is64Bit));
    }

    [Fact]
    public void Packages_leave_out_apple_and_collapse_update_receipts()
    {
        var receipts = new[]
        {
            "package-id: com.apple.pkg.MobileAssets\nversion: 1\nvolume: /\nlocation: /\ninstall-time: 1700000000\n",
            "package-id: com.adobe.acrobat.AcrobatDCUpd2500121184\nversion: 25.001.21184\nvolume: /\nlocation: /\ninstall-time: 1730000000\n",
            "package-id: com.adobe.acrobat.AcrobatDCUpd2500120476\nversion: 25.001.20476\nvolume: /\nlocation: /\ninstall-time: 1720000000\n",
            "package-id: org.openvpn.client.pkg\nversion: 3.5.0\nvolume: /\nlocation: /\ninstall-time: 1710000000\n",
            "nothing useful",
        }.Select(MacParsers.Receipt).OfType<MacParsers.PackageReceipt>();
        var packages = MacParsers.Packages(receipts);
        Assert.Equal([("com.adobe.acrobat.AcrobatDCUpd2500121184", "25.001.21184"), ("org.openvpn.client.pkg", "3.5.0")],
            packages.Select(p => (p.Name, p.Version)));
        Assert.Single(MacParsers.Packages([new("com.adobe.acrobat.AcroRdrSCADCUpd2600322075_MUI", "26.003.22075", new DateTime(2026, 11, 1)),
            new("com.adobe.acrobat.AcroRdrSCADCUpd2600221789_MUI", "26.002.21789", new DateTime(2026, 9, 1))]));
        // Ziffern am Ende einer gewöhnlichen Kennung sind kein Update: beide bleiben.
        Assert.Equal(2, MacParsers.Packages([new("com.vendor.tool123456", "1", null), new("com.vendor.tool654321", "2", null)]).Count);
        Assert.All(packages, p => Assert.Equal(AppKinds.InstallerPackage, p.Kind));
    }

    [Fact]
    public void Default_route_logins_and_password_age_are_read()
    {
        Assert.Equal("en0", MacParsers.DefaultInterface("   route to: default\ndestination: default\n  gateway: 192.168.2.1\n  interface: en0\n      flags: <UP,GATEWAY>\n"));
        Assert.Null(MacParsers.DefaultInterface("route: writing to routing socket: not in table"));

        var berlin = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
        var now = new DateTime(2026, 11, 10, 18, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 10, 7, 15, 0, DateTimeKind.Utc),
            MacParsers.LastLogin("max       console                   Tue Nov 10 08:15   still logged in\n\nwtmp begins Wed Jul 30 11:58:47 2026\n", now, berlin));
        // Ein Datum nach heute stammt aus dem Vorjahr: last nennt kein Jahr.
        Assert.Equal(2025, MacParsers.LastLogin("max  ttys000  Fri Dec 19 09:00 - 17:00  (08:00)", now, berlin)!.Value.Year);
        Assert.Null(MacParsers.LastLogin("\nwtmp begins Wed Jul 30 11:58:47 2026\n", now, berlin));

        Assert.Equal(new DateTime(2024, 10, 27, 3, 33, 20, 500, DateTimeKind.Utc), MacParsers.PasswordLastSet("dsAttrTypeNative:passwordLastSetTime: 1730000000.5"));
        Assert.Null(MacParsers.PasswordLastSet("No such key: accountPolicyData"));
    }

    private static Computer Mac(string cpu, string model, string osVersion) =>
        new() { Name = "mac", Platform = OsPlatform.MacOS, CpuName = cpu, Model = model, OsVersion = osVersion };

    [Fact]
    public void Mac_support_follows_the_model_and_the_installed_version()
    {
        Assert.Equal(("15", "10.15", null), (MacOsSupport.Major("15.6.1 (24G90)"), MacOsSupport.Major("10.15.7"), MacOsSupport.Major("unknown")));
        Assert.Equal("MacBookPro18,1", MacOsSupport.ModelIdentifier("MacBook Pro (16-inch, 2021) (MacBookPro18,1)"));
        Assert.Equal(("26", "15", "15", null), (MacOsSupport.FinalForIntel("MacBookPro16,1"), MacOsSupport.FinalForIntel("MacBookPro16,3"),
            MacOsSupport.FinalForIntel("MacBookPro15,2"), MacOsSupport.FinalForIntel("Mac15,6")));
        Assert.Equal("28", MacOsSupport.Latest(["26.1", "28.0 (28A1)", null]));
        Assert.Equal(MacOsSupport.LatestMajor, MacOsSupport.Latest(["15.1"]));
        // Eine gemeldete ältere Version senkt den Stand nie.
        MacOsSupport.Observe("15.1");
        Assert.NotEqual("15", MacOsSupport.CurrentLatest);

        // Apple Silicon auf einer älteren Version: Upgrade möglich, Updates noch (die aktuelle und die zwei davor).
        var silicon = MacOsSupport.Assess(Mac("Apple M1 Pro", "MacBook Pro (MacBookPro18,1)", "15.6 (24G84)"), "27")!;
        Assert.Equal((true, null, "27", true, 2027), (silicon.AppleSilicon, silicon.FinalMajor, silicon.UpgradeTo, silicon.SecurityUpdates, silicon.SecurityUntilYear));
        // Intel, auf seiner letzten Version, die keine Updates mehr bekommt: ersetzen.
        var intel = MacOsSupport.Assess(Mac("Quad-Core Intel Core i5 @ 2 GHz", "MacBook Pro (MacBookPro14,1)", "13.7.8 (22H730)"), "27")!;
        Assert.Equal(("13", null, false), (intel.FinalMajor, intel.UpgradeTo, intel.SecurityUpdates));
        // Unbekanntes Intel-Modell und Windows-Rechner: keine Aussage.
        Assert.Null(MacOsSupport.Assess(Mac("Intel Core i7", "Hackintosh", "13.0")));
        Assert.Null(MacOsSupport.Assess(new Computer { Name = "pc", CpuName = "Apple", OsVersion = "10.0.26100" }));

        Assert.True(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.MacOsSecurityUpdatesEnded, Operator = RuleOperator.Equals, Value = "true" },
            Mac("Intel Core i5", "MacBook Pro (MacBookPro14,1)", "13.7.8")));
        Assert.False(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.MacOsSecurityUpdatesEnded, Operator = RuleOperator.Equals, Value = "true" },
            Mac("Apple M3", "MacBook Air (Mac15,12)", MacOsSupport.LatestMajor + ".1")));
    }

    [Fact]
    public async Task Ip_packages_software_details_and_users_are_stored_and_found_by_rules()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("MAC-03")).Single().Id;
        var data = SimulatedInventory.Create("MAC-03");
        await store.SaveAsync(id, data);

        var mac = (await computers.GetDetailAsync(id))!;
        Assert.Equal(data.PrimaryIpAddress, mac.IpAddress);
        Assert.Contains(mac.Apps, a => a.Kind == AppKinds.InstallerPackage);
        Assert.Contains(mac.Apps, a => a.Kind == AppKinds.HomebrewFormula);
        Assert.Contains(mac.Software, s => s.Architecture == SoftwareArchitectures.Intel && s.Origin == SoftwareOrigins.Unknown);
        Assert.NotNull(Assert.Single(mac.LocalUsers).LastLogonUtc);

        bool Rule(RuleField field) => CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = RuleOperator.Equals, Value = "true" }, mac);
        Assert.True(Rule(RuleField.IntelOnlyApps));
        Assert.True(Rule(RuleField.UnknownDeveloperApps));
        // Auf einem Intel-Mac laufen Intel-Programme nativ: kein Rosetta.
        mac.CpuName = "Quad-Core Intel Core i5";
        Assert.False(Rule(RuleField.IntelOnlyApps));
        // Für Windows-Rechner gilt keine der Mac-Regeln.
        Assert.False(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.IntelOnlyApps, Operator = RuleOperator.Equals, Value = "false" }, new Computer { Name = "pc" }));
    }
}
