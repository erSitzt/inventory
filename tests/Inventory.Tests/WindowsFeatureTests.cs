using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

/// <summary>Optionale Windows-Features und WSL: Erfassung, Speicherung, Verlauf und Sammlungsregeln.</summary>
public sealed class WindowsFeatureTests : IDisposable
{
    private const string Anna = "S-1-5-21-1-2-3-1001";
    private const string Ben = "S-1-5-21-1-2-3-1002";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(@"\\?\D:\WSL2", null, @"D:\WSL2\ext4.vhdx")]
    [InlineData(@"\\?\C:\Users\anna\AppData\Local\Docker\wsl\main", "ext4.vhdx", @"C:\Users\anna\AppData\Local\Docker\wsl\main\ext4.vhdx")]
    [InlineData(@"C:\WSL\Ubuntu\", "", @"C:\WSL\Ubuntu\ext4.vhdx")]
    [InlineData(@"\\?\UNC\server\share\wsl", "disk.vhdx", @"\\server\share\wsl\disk.vhdx")]
    // Nur ein Dateiname gehört in VhdFileName; ein Pfad führte aus dem Ordner der Distribution hinaus.
    [InlineData(@"C:\WSL", @"..\..\Windows\System32\config\SAM", null)]
    [InlineData(@"C:\WSL", @"D:\other.vhdx", null)]
    [InlineData(null, "ext4.vhdx", null)]
    [InlineData("  ", null, null)]
    public void The_virtual_disk_of_a_distribution_is_found_from_its_registration(string? basePath, string? vhdFileName, string? expected) =>
        Assert.Equal(expected, WslNames.DiskPath(basePath, vhdFileName));

    private static InventoryData Scan(bool wslInstalled = true, params (string Sid, bool Loaded)[] profiles) => new()
    {
        ComputerName = "PC-WSL",
        OptionalFeatures = [new OptionalFeatureItem { Name = "VirtualMachinePlatform", Caption = "VM-Plattform" }],
        Wsl = new WslInfo { Installed = wslInstalled, Version = wslInstalled ? "2.3.26.0" : null },
        UserProfiles = [.. profiles.Select(p => new UserProfileState { Sid = p.Sid, Loaded = p.Loaded, Name = p.Sid == Anna ? @"CORP\anna" : @"CORP\ben" })],
    };

    private static ComputerWslDistro Stored(string name, string sid) =>
        new() { Name = name, UserSid = sid, UserName = sid == Anna ? @"CORP\anna" : @"CORP\ben", WslVersion = 2, LastUsedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), SizeBytes = 42 };

    [Fact]
    public void Distributions_of_a_logged_off_user_are_kept_and_a_readable_or_deleted_profile_decides()
    {
        var current = Scan(true, (Anna, false), (Ben, true));
        current.Wsl!.Distros.Add(new WslDistroItem { Name = "Debian", UserSid = Ben, WslVersion = 2 });
        // Annas Ubuntu bleibt; Bens Ubuntu hat Ben wirklich entfernt (sein Profil war lesbar); Carl gibt es nicht mehr.
        Assert.Equal(1, WslDistros.CarryOver([Stored("Ubuntu", Anna), Stored("Ubuntu", Ben), Stored("Kali", "S-1-5-21-1-2-3-1003")], current));
        var kept = current.Wsl.Distros.Single(d => d.Name == "Ubuntu");
        Assert.True(kept is { UserSid: Anna, CarriedOver: true, WslVersion: 2, SizeBytes: 42 });
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), kept.LastUsedUtc);
        Assert.Equal(["Debian", "Ubuntu"], current.Wsl.Distros.Select(d => d.Name).Order());
    }

    [Fact]
    public void Without_wsl_or_from_an_older_runner_nothing_is_carried_over()
    {
        var removed = Scan(false, (Anna, false));
        Assert.Equal(0, WslDistros.CarryOver([Stored("Ubuntu", Anna)], removed));
        Assert.Empty(removed.Wsl!.Distros);

        var oldRunner = new InventoryData();
        Assert.Equal(0, WslDistros.CarryOver([Stored("Ubuntu", Anna)], oldRunner));
        Assert.Null(oldRunner.Wsl);
    }

    [Fact]
    public async Task Features_and_wsl_are_stored_kept_for_older_runners_and_compared_from_the_second_scan_on()
    {
        var computers = new ComputerService(_db, new SimulatedHostProbe());
        var computer = (await computers.ImportAsync("PC-WSL")).Single();
        var store = new InventoryStore(_db);

        // Ein älterer Runner liefert beides nicht: nichts gespeichert, nichts als "neu" gemeldet.
        await store.SaveAsync(computer.Id, new InventoryData { ComputerName = "PC-WSL" });
        var stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.Null(stored.FeaturesScanUtc);
        Assert.Null(stored.WslInstalled);

        var data = Scan(true, (Anna, true));
        data.Wsl!.Distros.Add(new WslDistroItem { Name = "Ubuntu-24.04", UserSid = Anna, UserName = @"CORP\anna", WslVersion = 2, LastUsedUtc = DateTime.UtcNow.AddDays(-3), SizeBytes = 8L << 30 });
        await store.SaveAsync(computer.Id, data);
        stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.NotNull(stored.FeaturesScanUtc);
        Assert.Equal(["VirtualMachinePlatform"], stored.OptionalFeatures.Select(f => f.Name));
        Assert.Equal("VM-Plattform", stored.OptionalFeatures[0].Caption);
        Assert.True(stored.WslInstalled);
        Assert.Equal("2.3.26.0", stored.WslVersion);
        Assert.True(stored.WslDistros.Single() is { Name: "Ubuntu-24.04", UserName: @"CORP\anna", WslVersion: 2, CarriedOver: false });
        // Der erste Scan mit Features und WSL ist die Ausgangslage.
        Assert.DoesNotContain(await store.ChangesAsync(computer.Id), c => c.Change.Category is "Windows feature" or "WSL distribution" || c.Change.Item == "WSL");

        // Anna ist abgemeldet, ein Feature ist dazugekommen, WSL ist aktualisiert und Ben hat eine Distribution.
        data = Scan(true, (Anna, false), (Ben, true));
        data.OptionalFeatures!.Add(new OptionalFeatureItem { Name = "Microsoft-Hyper-V-All", Caption = "Hyper-V" });
        data.Wsl!.Version = "2.6.1.0";
        data.Wsl.Distros.Add(new WslDistroItem { Name = "docker-desktop", UserSid = Ben, UserName = @"CORP\ben", WslVersion = 2 });
        await store.SaveAsync(computer.Id, data);
        stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.Equal(["docker-desktop", "Ubuntu-24.04"], stored.WslDistros.Select(d => d.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(stored.WslDistros.Single(d => d.Name == "Ubuntu-24.04").CarriedOver);
        var changes = (await store.ChangesAsync(computer.Id)).Select(c => $"{c.Change.Category}|{c.Change.Kind}|{c.Change.Item}|{c.Change.OldValue}|{c.Change.NewValue}").ToList();
        Assert.Contains("Windows feature|Added|Microsoft-Hyper-V-All||", changes);
        Assert.Contains("System|Changed|WSL|2.3.26.0|2.6.1.0", changes);
        Assert.Contains(@"WSL distribution|Added|docker-desktop (CORP\ben)||WSL 2", changes);
        // Die übernommene Distribution ist nicht verschwunden und wieder aufgetaucht.
        Assert.DoesNotContain(changes, c => c.Contains("Ubuntu-24.04"));

        // Ein Scan, der beides nicht lesen konnte, lässt den Stand stehen.
        await store.SaveAsync(computer.Id, new InventoryData { ComputerName = "PC-WSL" });
        stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.Equal(2, stored.OptionalFeatures.Count);
        Assert.Equal(2, stored.WslDistros.Count);

        // WSL entfernt: Die Distributionen gehen mit.
        await store.SaveAsync(computer.Id, Scan(false, (Anna, false), (Ben, true)));
        stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.False(stored.WslInstalled);
        Assert.Empty(stored.WslDistros);
        Assert.Contains((await store.ChangesAsync(computer.Id)).Select(c => c.Change), c => c is { Item: "WSL", OldValue: "2.6.1.0", NewValue: "not installed" });
    }

    [Fact]
    public void Collection_rules_find_features_and_wsl_and_unknown_values_do_not_match()
    {
        var now = DateTime.UtcNow;
        var computer = new Computer
        {
            Name = "PC",
            FeaturesScanUtc = now,
            WslScanUtc = now,
            OptionalFeatures = [new ComputerOptionalFeature { Name = "Microsoft-Windows-Subsystem-Linux", Caption = "Windows-Subsystem für Linux" }, new ComputerOptionalFeature { Name = "NetFx3" }],
            WslInstalled = true,
            WslDistros = [new ComputerWslDistro { Name = "Ubuntu-24.04", LastUsedUtc = now.AddDays(-40) }, new ComputerWslDistro { Name = "docker-desktop", LastUsedUtc = now.AddDays(-2).AddHours(-1) }, new ComputerWslDistro { Name = "Alt" }],
        };
        bool Matches(RuleField field, RuleOperator op, string value, Computer? target = null) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, target ?? computer);

        Assert.True(Matches(RuleField.WindowsFeature, RuleOperator.Equals, "netfx3"));
        Assert.True(Matches(RuleField.WindowsFeature, RuleOperator.Contains, "Subsystem für Linux"));
        Assert.True(Matches(RuleField.WindowsFeature, RuleOperator.NotContains, "Hyper-V"));
        Assert.False(Matches(RuleField.WindowsFeature, RuleOperator.NotEquals, "NetFx3"));

        Assert.True(Matches(RuleField.WslInstalled, RuleOperator.Equals, "true"));
        // Die jüngste Nutzung zählt: vor zwei Tagen.
        Assert.True(Matches(RuleField.WslDaysSinceUse, RuleOperator.LessThan, "7"));
        Assert.False(Matches(RuleField.WslDaysSinceUse, RuleOperator.GreaterOrEqual, "30"));
        Assert.True(Matches(RuleField.WslDistro, RuleOperator.StartsWith, "ubuntu"));
        Assert.True(Matches(RuleField.WslDistro, RuleOperator.NotContains, "kali"));

        // Noch nicht erfasst: weder "eingerichtet" noch "nicht eingerichtet", ohne Datum keine Tage, und auch eine verneinte
        // Regel trifft nicht, nur weil die Listen leer sind.
        var unknown = new Computer { Name = "ALT", WslDistros = [new ComputerWslDistro { Name = "Ubuntu" }] };
        Assert.False(Matches(RuleField.WslInstalled, RuleOperator.Equals, "true", unknown));
        Assert.False(Matches(RuleField.WslInstalled, RuleOperator.Equals, "false", unknown));
        Assert.False(Matches(RuleField.WindowsFeature, RuleOperator.NotContains, "Hyper-V", unknown));
        Assert.False(Matches(RuleField.WindowsFeature, RuleOperator.NotEquals, "NetFx3", unknown));
        Assert.False(Matches(RuleField.WslDistro, RuleOperator.NotContains, "kali", new Computer { Name = "MAC" }));
        Assert.False(Matches(RuleField.WslDistro, RuleOperator.NotEquals, "Ubuntu", new Computer { Name = "MAC" }));
        Assert.False(Matches(RuleField.WslDaysSinceUse, RuleOperator.GreaterOrEqual, "0", unknown));
        Assert.False(Matches(RuleField.WslDaysSinceUse, RuleOperator.LessThan, "100000", unknown));
    }

    [Fact]
    public void The_simulation_has_features_everywhere_and_wsl_on_some_computers()
    {
        var scans = Enumerable.Range(1, 40).Select(i => SimulatedInventory.Create($"PC-{i:D3}")).ToList();
        Assert.All(scans, s => Assert.NotEmpty(s.OptionalFeatures!));
        Assert.All(scans, s => Assert.NotNull(s.Wsl));
        Assert.Contains(scans, s => s.Wsl!.Installed && s.Wsl.Distros.Count > 0);
        Assert.Contains(scans, s => !s.Wsl!.Installed);
        Assert.Null(SimulatedInventory.Create("MAC-01").Wsl);
    }
}
