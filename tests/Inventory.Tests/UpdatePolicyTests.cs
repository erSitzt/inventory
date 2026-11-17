using System.Globalization;
using System.Text;
using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

/// <summary>Windows-Update-Richtlinie: Werte aus RSoP, Auslegung, Speicherung, Verlauf, Regeln, Übersicht und Berichte.</summary>
public sealed class UpdatePolicyTests : IDisposable
{
    private static readonly DateTime Applied = new(2026, 11, 17, 6, 30, 0, DateTimeKind.Utc);
    private static readonly PassthroughLocalizer L = PassthroughLocalizer.Instance;

    private readonly TestDb _db = new();
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    // Wochentage kommen aus der Kultur der Oberfläche.
    public UpdatePolicyTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        _db.Dispose();
    }

    /// <summary>Ring A setzt die Installationszeit; Ring B setzt sie auf 22 Uhr und wird überstimmt.</summary>
    private static InventoryData Scan(string time, List<string>? groups, string name = "SRV-01") => new()
    {
        ComputerName = name,
        UpdatePolicy = new UpdatePolicyInfo
        {
            RsopRead = true, Groups = groups, GroupPolicyAppliedUtc = Applied,
            Settings =
            [
                Gpo(UpdatePolicyKeys.WindowsUpdate, "WUServer", "http://wsus.corp.example:8530", "WSUS"),
                Gpo(UpdatePolicyKeys.AutoUpdate, "UseWUServer", "1", "WSUS"),
                Gpo(UpdatePolicyKeys.WindowsUpdate, "TargetGroupEnabled", "1", "Ring A"),
                Gpo(UpdatePolicyKeys.WindowsUpdate, "TargetGroup", "Servers-A", "Ring A"),
                Gpo(UpdatePolicyKeys.AutoUpdate, "AUOptions", "4", "Ring A"),
                Gpo(UpdatePolicyKeys.AutoUpdate, "ScheduledInstallDay", "1", "Ring A"),
                new UpdatePolicySetting
                {
                    Source = UpdatePolicySources.Gpo, Key = UpdatePolicyKeys.AutoUpdate, Name = "ScheduledInstallTime", Value = time, Gpo = "Ring A",
                    AlsoSetBy = [new UpdatePolicyOverride { Gpo = "Ring B", Value = "22" }],
                },
            ],
        },
    };

    private static UpdatePolicySetting Gpo(string key, string name, string value, string gpo) =>
        new() { Source = UpdatePolicySources.Gpo, Key = key, Name = name, Value = value, Gpo = gpo };

    private static List<ComputerUpdatePolicySetting> Entities(InventoryData data) =>
        data.UpdatePolicy!.Settings.Select(s => UpdatePolicies.ToEntity(s, 1)).ToList();

    private static ComputerUpdatePolicySetting Mdm(string name, string value) =>
        new() { Source = UpdatePolicySources.Mdm, Key = UpdatePolicyKeys.Mdm, Name = name, Value = value };

    [Fact]
    public void Rsop_values_read_like_registry_values()
    {
        Assert.Equal("4", RsopValues.Decode([4, 0, 0, 0], 4));
        Assert.Equal("4294967295", RsopValues.Decode([0xFF, 0xFF, 0xFF, 0xFF], 4));
        Assert.Equal("5", RsopValues.Decode(BitConverter.GetBytes(5UL), 11));
        Assert.Equal("http://wsus:8530", RsopValues.Decode(Encoding.Unicode.GetBytes("http://wsus:8530\0"), 1));
        Assert.Equal("a; b", RsopValues.Decode(Encoding.Unicode.GetBytes("a\0b\0\0"), 7));
        Assert.Equal("01AB", RsopValues.Decode([0x01, 0xAB], 3));
        Assert.Null(RsopValues.Decode(null, 4));
        // Aus der Registry liest .NET ein DWORD über 2^31 als negative Zahl.
        Assert.Equal("4294967295", RsopValues.Format(-1));
        Assert.Equal("a; b", RsopValues.Format(new[] { "a", "b" }));
        Assert.Equal(RsopValues.MaxLength, RsopValues.Format(new string('x', 1000))!.Length);
    }

    [Theory]
    [InlineData(@"Software\Policies\Microsoft\Windows\WindowsUpdate", UpdatePolicyKeys.WindowsUpdate)]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU\", UpdatePolicyKeys.AutoUpdate)]
    [InlineData(@"Software\Policies\Microsoft\Windows\WindowsUpdate\AU\Other", null)]
    [InlineData(@"Software\Policies\Microsoft\Windows Defender", null)]
    [InlineData(null, null)]
    public void Rsop_registry_keys_map_to_the_policy_keys(string? path, string? expected) => Assert.Equal(expected, UpdatePolicyKeys.FromPolicyPath(path));

    [Fact]
    public void The_summary_reads_source_schedule_target_group_and_conflicts()
    {
        var settings = Entities(Scan("3", null));
        var summary = UpdatePolicies.Summarize(settings, L);
        Assert.Equal("http://wsus.corp.example:8530", summary.Source);
        Assert.Equal("Servers-A", summary.TargetGroup);
        Assert.Equal("Download and install on a schedule: Sunday 03:00", summary.Automatic);
        Assert.Equal(["Ring A", "WSUS"], summary.Gpos);
        Assert.True(summary.Conflicts);
        // Setzt die überstimmte GPO denselben Wert, ist das kein Konflikt.
        Assert.False(UpdatePolicies.Summarize(Entities(Scan("22", null)), L).Conflicts);

        var time = settings.Single(s => s.Name == "ScheduledInstallTime");
        Assert.Equal(("Scheduled install time", "03:00"), (UpdatePolicies.Label(time, L), UpdatePolicies.Display(time, L)));
        Assert.Equal([("Ring B", "22")], time.Overrides);
        Assert.Equal("yes", UpdatePolicies.Display(settings.Single(s => s.Name == "UseWUServer"), L));
        var unknown = Mdm("SomethingNew", "7");
        Assert.Equal(("SomethingNew", "7"), (UpdatePolicies.Label(unknown, L), UpdatePolicies.Display(unknown, L)));

        // Nur Intune, täglich um 3 Uhr.
        var intune = UpdatePolicies.Summarize([Mdm("AllowAutoUpdate", "2"), Mdm("ScheduledInstallDay", "0"), Mdm("ScheduledInstallTime", "3")], L);
        Assert.Equal(("Windows Update for Business (Intune)", "Install and restart at the scheduled time: Every day 03:00"), (intune.Source, intune.Automatic));
        Assert.Empty(intune.Gpos);
        // Ausgeschaltet, ohne WSUS; die Zielgruppe gilt nur mit eingeschalteter clientseitiger Zuordnung.
        var off = UpdatePolicies.Summarize(
        [
            new ComputerUpdatePolicySetting { Source = UpdatePolicySources.Registry, Key = UpdatePolicyKeys.AutoUpdate, Name = "NoAutoUpdate", Value = "1" },
            new ComputerUpdatePolicySetting { Source = UpdatePolicySources.Registry, Key = UpdatePolicyKeys.WindowsUpdate, Name = "TargetGroup", Value = "Old" },
        ], L);
        Assert.Equal(("Microsoft Update", "off", null), (off.Source, off.Automatic, off.TargetGroup));
        var none = UpdatePolicies.Summarize([], L);
        Assert.Equal((null, null, null, false, null), (none.Source, none.Automatic, none.TargetGroup, none.Conflicts, none.Deferral));
        Assert.Equal("", UpdatePolicies.Fingerprint([]));

        // Zurückstellung: bei Intune die Tage allein, in der Gruppenrichtlinie nur mit eingeschaltetem Schalter; 0 Tage = keine.
        Assert.Equal("Deferred: quality updates 7 days",
            UpdatePolicies.Summarize([Mdm("DeferQualityUpdatesPeriodInDays", "7"), Mdm("DeferFeatureUpdatesPeriodInDays", "0")], L).Deferral);
        ComputerUpdatePolicySetting Policy(string name, string value) =>
            new() { Source = UpdatePolicySources.Gpo, Key = UpdatePolicyKeys.WindowsUpdate, Name = name, Value = value, Gpo = "WUfB" };
        Assert.Null(UpdatePolicies.Summarize([Policy("DeferQualityUpdatesPeriodInDays", "14")], L).Deferral);
        Assert.Equal("Deferred: quality updates 14 days, feature updates 30 days", UpdatePolicies.Summarize(
        [
            Policy("DeferQualityUpdates", "1"), Policy("DeferQualityUpdatesPeriodInDays", "14"),
            Policy("DeferFeatureUpdates", "1"), Policy("DeferFeatureUpdatesPeriodInDays", "30"),
        ], L).Deferral);
    }

    [Fact]
    public async Task The_policy_is_stored_with_its_gpos_and_compared_from_the_second_scan_on()
    {
        var computers = new ComputerService(_db, new SimulatedHostProbe());
        var computer = (await computers.ImportAsync("SRV-01")).Single();
        var store = new InventoryStore(_db);
        async Task<List<string>> PolicyChanges() => (await store.ChangesAsync(computer.Id))
            .Where(c => c.Change.Category is "Windows Update policy" or "Policy group")
            .Select(c => $"{c.Change.Category}|{c.Change.Kind}|{c.Change.Item}|{c.Change.OldValue}|{c.Change.NewValue}").ToList();

        // Ein älterer Runner liefert nichts: nicht erfasst.
        await store.SaveAsync(computer.Id, new InventoryData { ComputerName = "SRV-01" });
        Assert.Null((await computers.GetDetailAsync(computer.Id))!.UpdatePolicyScanUtc);

        await store.SaveAsync(computer.Id, Scan("3", ["Domain Computers", "WSUS-Ring-A"]));
        var stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.NotNull(stored.UpdatePolicyScanUtc);
        Assert.Equal(Applied, DateTime.SpecifyKind(stored.GroupPolicyAppliedUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(["Domain Computers", "WSUS-Ring-A"], stored.PolicyGroupNames);
        Assert.Equal(7, stored.UpdatePolicySettings.Count);
        var time = stored.UpdatePolicySettings.Single(s => s.Name == "ScheduledInstallTime");
        Assert.Equal((UpdatePolicySources.Gpo, UpdatePolicyKeys.AutoUpdate, "3", "Ring A"), (time.Source, time.Key, time.Value, time.Gpo));
        Assert.Equal([("Ring B", "22")], time.Overrides);
        // Der erste Scan mit Richtlinie ist die Ausgangslage.
        Assert.Empty(await PolicyChanges());

        // Ring A installiert jetzt um 22 Uhr, und der Server ist über SQL-Servers verschachtelt in einer weiteren Gruppe.
        await store.SaveAsync(computer.Id, Scan("22", ["Domain Computers", "SQL-Servers", "WSUS-Ring-A"]));
        var changes = await PolicyChanges();
        Assert.Equal(2, changes.Count);
        Assert.Contains("Windows Update policy|Changed|ScheduledInstallTime|3 (Ring A)|22 (Ring A)", changes);
        Assert.Contains("Policy group|Added|SQL-Servers||", changes);

        // RSoP diesmal nicht lesbar: Die Werte behalten ihre GPO, die Gruppen bleiben, der Verlauf bleibt ruhig.
        var unreadable = Scan("22", null);
        unreadable.UpdatePolicy!.RsopRead = false;
        foreach (var setting in unreadable.UpdatePolicy.Settings)
            (setting.Source, setting.Gpo, setting.AlsoSetBy) = (UpdatePolicySources.Registry, null, []);
        await store.SaveAsync(computer.Id, unreadable);
        stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.All(stored.UpdatePolicySettings, s => Assert.NotNull(s.Gpo));
        Assert.Equal([("Ring B", "22")], stored.UpdatePolicySettings.Single(s => s.Name == "ScheduledInstallTime").Overrides);
        Assert.Equal(3, stored.PolicyGroupNames.Count());
        Assert.Equal(2, (await PolicyChanges()).Count);

        // Ein Scan ohne Richtlinie lässt den Stand stehen.
        await store.SaveAsync(computer.Id, new InventoryData { ComputerName = "SRV-01" });
        Assert.Equal(7, (await computers.GetDetailAsync(computer.Id))!.UpdatePolicySettings.Count);
    }

    [Fact]
    public void Collection_rules_find_gpos_target_groups_and_nested_groups_and_unknown_values_do_not_match()
    {
        var computer = new Computer
        {
            Name = "SRV-01", UpdatePolicyScanUtc = DateTime.UtcNow, PolicyGroups = "Domain Computers\nSQL-Servers\nWSUS-Ring-A",
            UpdatePolicySettings = Entities(Scan("3", null)),
        };
        bool Matches(RuleField field, RuleOperator op, string value, Computer? target = null) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, target ?? computer);

        Assert.True(Matches(RuleField.UpdateGpo, RuleOperator.Equals, "ring a"));
        Assert.True(Matches(RuleField.UpdateGpo, RuleOperator.NotEquals, "Ring B"));
        Assert.False(Matches(RuleField.UpdateGpo, RuleOperator.NotContains, "Ring"));
        Assert.True(Matches(RuleField.WsusTargetGroup, RuleOperator.Equals, "Servers-A"));
        Assert.True(Matches(RuleField.PolicyGroup, RuleOperator.Equals, "SQL-Servers"));
        Assert.False(Matches(RuleField.PolicyGroup, RuleOperator.Equals, "Clients"));

        // Noch nicht erfasst: Auch verneinte Regeln treffen nicht.
        var unknown = new Computer { Name = "ALT" };
        Assert.False(Matches(RuleField.UpdateGpo, RuleOperator.NotEquals, "Ring A", unknown));
        Assert.False(Matches(RuleField.WsusTargetGroup, RuleOperator.NotEquals, "Servers-A", unknown));
        Assert.False(Matches(RuleField.PolicyGroup, RuleOperator.NotContains, "Ring", unknown));
        // Bekannt, aber ohne Gruppen: Dann trifft die verneinte Regel.
        Assert.True(Matches(RuleField.PolicyGroup, RuleOperator.NotContains, "Ring", new Computer { Name = "PC", PolicyGroups = "" }));
    }

    [Fact]
    public async Task The_overview_groups_computers_with_the_same_policy_and_lists_those_not_captured()
    {
        var computers = new ComputerService(_db, new SimulatedHostProbe());
        var ids = (await computers.ImportAsync("SRV-01\nSRV-02\nSRV-03\nPC-01\nPC-02\nMAC-01")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var store = new InventoryStore(_db);
        await store.SaveAsync(ids["SRV-01"], Scan("3", [], "SRV-01"));
        await store.SaveAsync(ids["SRV-02"], Scan("3", [], "SRV-02"));
        await store.SaveAsync(ids["SRV-03"], Scan("22", [], "SRV-03"));
        await store.SaveAsync(ids["PC-01"], new InventoryData { ComputerName = "PC-01", UpdatePolicy = new UpdatePolicyInfo() });
        await using (var context = _db.CreateDbContext())
        {
            (await context.Computers.FindAsync(ids["MAC-01"]))!.Platform = OsPlatform.MacOS;
            await context.SaveChangesAsync();
        }

        var overview = await new UpdatePolicyService(_db).OverviewAsync();
        Assert.Equal(3, overview.Policies.Count);
        Assert.Equal(["SRV-01", "SRV-02"], overview.Policies[0].Computers.Select(c => c.Name.ToUpperInvariant()));
        Assert.All(overview.Policies[0].Computers, c => Assert.True(c.Conflicts));
        Assert.Equal(["SRV-03"], overview.Policies[1].Computers.Select(c => c.Name.ToUpperInvariant()));
        Assert.True(overview.Policies[2] is { NotConfigured: true });
        Assert.Equal(2, overview.ComputersWithConflicts);
        // Macs haben keine Windows-Update-Richtlinie.
        Assert.Equal(["PC-02"], overview.NotCaptured.Select(c => c.Name.ToUpperInvariant()));
    }

    [Fact]
    public async Task Reports_show_source_target_group_schedule_gpos_conflicts_and_groups()
    {
        var computers = new ComputerService(_db, new SimulatedHostProbe());
        var ids = (await computers.ImportAsync("SRV-01\nPC-01")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        await new InventoryStore(_db).SaveAsync(ids["SRV-01"], Scan("3", ["Domain Computers", "WSUS-Ring-A"]));

        var table = await new ReportService(_db).TableAsync(new ReportQuery(ReportSource.Computers,
            Columns: ["name", "updateSource", "wsusTargetGroup", "automaticUpdates", "updateGpos", "updatePolicyConflicts", "policyGroups"], SortColumn: "name"));
        Assert.Equal(["PC-01||||||", "SRV-01|http://wsus.corp.example:8530|Servers-A|Download and install on a schedule: Sunday 03:00|Ring A, WSUS|yes|Domain Computers, WSUS-Ring-A"],
            table.Rows.Select(r => string.Join('|', r)));
    }

    [Fact]
    public void The_simulation_has_wsus_rings_for_servers_and_intune_for_clients()
    {
        var sql = SimulatedInventory.Create("SRV-SQL02").UpdatePolicy!;
        Assert.Contains("SQL-Servers", sql.Groups!);
        Assert.Contains(sql.Settings, s => s.Name == "ScheduledInstallTime" && s.AlsoSetBy.Count == 1);
        Assert.True(UpdatePolicies.Summarize(sql.Settings.Select(s => UpdatePolicies.ToEntity(s, 1)).ToList(), L).Conflicts);

        var clients = Enumerable.Range(1, 30).Select(i => SimulatedInventory.Create($"PC-{i:D3}").UpdatePolicy!).ToList();
        Assert.Contains(clients, p => p.Settings.Count > 0 && p.Settings.All(s => s.Source == UpdatePolicySources.Mdm));
        Assert.Contains(clients, p => p.Settings.Count == 0);
        Assert.Null(SimulatedInventory.Create("MAC-01").UpdatePolicy);
    }
}
