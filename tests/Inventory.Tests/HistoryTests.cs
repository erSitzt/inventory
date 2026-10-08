using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class HistoryTests
{
    private static InventoryData Scan(string chrome, params string[] admins) => new()
    {
        ComputerName = "PC-01",
        OsVersion = "10.0.26100.1",
        MemoryBytes = 16L * 1024 * 1024 * 1024,
        Software = [new SoftwareItem { Name = "Google Chrome", Version = chrome }, new SoftwareItem { Name = "7-Zip", Version = "24.08" }],
        Services = [new ServiceItem { Name = "Spooler", StartMode = "Auto" }, new ServiceItem { Name = "AarSvc_1c86fa", StartMode = "Manual" }],
        LocalUsers = [new LocalUserItem { Name = "Administrator", Disabled = true }],
        LocalAdmins = admins.Select(a => new LocalAdminItem { Name = a }).ToList(),
    };

    [Fact]
    public async Task First_scan_is_the_baseline_and_later_scans_record_what_changed()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;

        await store.SaveAsync(id, Scan("139.0", @"PC-01\Administrator"));
        Assert.Empty(await store.ChangesAsync(id));
        await store.SaveAsync(id, Scan("139.0", @"PC-01\Administrator"));
        Assert.Empty(await store.ChangesAsync(id));

        var second = Scan("141.0", @"PC-01\Administrator", @"CORP\max");
        second.Software.RemoveAll(s => s.Name == "7-Zip");
        second.Software.Add(new SoftwareItem { Name = "VLC", Version = "3.0.21" });
        second.OsVersion = "10.0.26100.2";
        second.MemoryBytes = 32L * 1024 * 1024 * 1024;
        second.Services = [new ServiceItem { Name = "Spooler", StartMode = "Disabled" }, new ServiceItem { Name = "AarSvc_99aa01", StartMode = "Manual" }];
        second.LocalUsers[0].Disabled = false;
        await store.SaveAsync(id, second);

        var changes = (await store.ChangesAsync(id)).Select(c => $"{c.Change.Category}|{c.Change.Kind}|{c.Change.Item}|{c.Change.OldValue}|{c.Change.NewValue}").Order().ToList();
        Assert.Equal(
        [
            "Dienst|Changed|Spooler|Auto|Disabled",
            "Lokaler Administrator|Added|CORP\\max||",
            "Lokaler Benutzer|Changed|Administrator|deaktiviert|aktiv",
            "Software|Added|VLC||3.0.21",
            "Software|Changed|Google Chrome|139.0|141.0",
            "Software|Removed|7-Zip|24.08|",
            "System|Changed|Arbeitsspeicher|16 GB|32 GB",
            "System|Changed|Windows-Version|10.0.26100.1|10.0.26100.2",
        ], changes);
        Assert.Equal("pc-01", (await store.ChangesAsync())[0].Computer);

        var detail = await computers.GetDetailAsync(id);
        Assert.Equal([@"CORP\max", @"PC-01\Administrator"], detail!.LocalAdmins.Select(a => a.Name).Order());
        Assert.False(Assert.Single(detail.LocalUsers).Disabled);

        Assert.Equal(0, await store.CleanupChangesAsync(TimeSpan.FromDays(365)));
        Assert.Equal(8, await store.CleanupChangesAsync(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Local_admin_rule_matches_any_member_and_its_negation_matches_none()
    {
        var computer = new Computer { Name = "pc", LocalAdmins = [new ComputerLocalAdmin { Name = @"PC\Administrator" }, new ComputerLocalAdmin { Name = @"CORP\max" }] };
        static CollectionRule Rule(RuleOperator op, string value) => new() { Field = RuleField.LocalAdmin, Operator = op, Value = value };

        Assert.True(CollectionEvaluator.Matches(Rule(RuleOperator.Contains, "max"), computer));
        Assert.False(CollectionEvaluator.Matches(Rule(RuleOperator.NotContains, "max"), computer));
        Assert.True(CollectionEvaluator.Matches(Rule(RuleOperator.NotContains, "erika"), computer));
    }
}

public class SecurityStatusTests
{
    private static InventoryData Scan(bool firewallDomain, bool realTime, params string[] pending) => new()
    {
        ComputerName = "PC-01",
        Security = new SecurityStatus
        {
            FirewallDomain = firewallDomain, FirewallPrivate = true, FirewallPublic = true,
            DefenderEnabled = true, DefenderRealTime = realTime, AntivirusProducts = ["Windows Defender"],
            UpdateLastInstallUtc = DateTime.UtcNow.AddDays(-40), UpdateRebootRequired = pending.Length > 0, UpdatePending = [.. pending],
        },
    };

    [Fact]
    public async Task Security_status_is_stored_feeds_rules_and_changes_show_up_in_the_history()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;
        static bool Matches(Computer c, RuleField field, RuleOperator op, string value) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, c);

        await store.SaveAsync(id, Scan(firewallDomain: true, realTime: true));
        var healthy = (await computers.GetAsync(id))!;
        Assert.False(healthy.FirewallOff);
        Assert.False(healthy.AntivirusProblem);
        Assert.Equal(0, healthy.UpdatePendingCount);
        Assert.True(Matches(healthy, RuleField.DaysSinceUpdate, RuleOperator.GreaterOrEqual, "30"));
        Assert.False(Matches(healthy, RuleField.PendingUpdates, RuleOperator.GreaterOrEqual, "1"));

        await store.SaveAsync(id, Scan(firewallDomain: false, realTime: false, "KB1", "KB2"));
        var broken = (await computers.GetAsync(id))!;
        Assert.True(broken.FirewallOff);
        Assert.True(broken.AntivirusProblem);
        Assert.Equal("KB1\nKB2", broken.UpdatePending);
        Assert.True(Matches(broken, RuleField.FirewallOff, RuleOperator.Equals, "true"));
        Assert.True(Matches(broken, RuleField.AntivirusProblem, RuleOperator.Equals, "true"));
        Assert.True(Matches(broken, RuleField.RebootRequired, RuleOperator.Equals, "true"));
        Assert.True(Matches(broken, RuleField.PendingUpdates, RuleOperator.GreaterOrEqual, "2"));

        var changes = (await store.ChangesAsync(id)).Select(c => $"{c.Change.Item}: {c.Change.OldValue} → {c.Change.NewValue}").Order().ToList();
        Assert.Equal(["Defender-Echtzeitschutz: an → aus", "Firewall (Domäne): an → aus"], changes);
    }

    [Fact]
    public void Another_registered_antivirus_counts_as_protection_and_unscanned_computers_are_not_flagged()
    {
        var withOther = new Computer { Name = "a", LastScanUtc = DateTime.UtcNow, DefenderEnabled = false, DefenderRealTime = false, AntivirusProducts = "Windows Defender, Sophos Endpoint" };
        var unprotected = new Computer { Name = "b", LastScanUtc = DateTime.UtcNow, DefenderEnabled = false, DefenderRealTime = false, AntivirusProducts = "Windows Defender" };
        var unscanned = new Computer { Name = "c" };

        Assert.False(withOther.AntivirusProblem);
        Assert.True(unprotected.AntivirusProblem);
        Assert.False(unscanned.AntivirusProblem);
        Assert.False(unscanned.FirewallOff);
    }
}
