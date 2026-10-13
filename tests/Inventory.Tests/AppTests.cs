using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public sealed class AppTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("Microsoft.BingWeather", true)]
    [InlineData("MSTeams", true)]
    [InlineData("Microsoft.Xbox*", true)]
    [InlineData("*BingWeather*", true)]
    [InlineData("Microsoft.Bing*.Preview", true)]
    // Zu weit gefasst: alles, alles eines Herstellers, zu wenig fester Text.
    [InlineData("*", false)]
    [InlineData("***", false)]
    [InlineData("Microsoft*", false)]
    [InlineData("Microsoft.*", false)]
    [InlineData("MS*", false)]
    [InlineData("App", false)]
    [InlineData("*Bing*", false)]
    // Nichts, was aus dem Skript ausbrechen könnte.
    [InlineData("Microsoft.Bing'; Remove-Item C:\\ -Recurse; '", false)]
    [InlineData("Microsoft.Bing Weather", false)]
    [InlineData("Microsoft.Bing$(x)", false)]
    [InlineData("Microsoft.Bing`n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_narrow_package_names_are_accepted_for_removal(string? pattern, bool expected) =>
        Assert.Equal(expected, AppNames.IsRemovalPattern(pattern));

    [Fact]
    public void The_removal_script_checks_before_it_removes_and_rejects_bad_names()
    {
        var script = AppNames.RemovalScript("Microsoft.BingWeather", keepProvisioned: false);
        Assert.Contains("$pattern = 'Microsoft.BingWeather'", script);
        // Erst zählen, dann entfernen: die Grenze steht vor dem ersten Remove-Aufruf.
        Assert.True(script.IndexOf("-gt " + AppNames.MaxMatches, StringComparison.Ordinal) < script.IndexOf("Remove-AppxPackage ", StringComparison.Ordinal));
        Assert.True(script.IndexOf("-gt " + AppNames.MaxMatches, StringComparison.Ordinal) < script.IndexOf("Remove-AppxProvisionedPackage ", StringComparison.Ordinal));
        Assert.Contains("-not $_.IsFramework", script);
        Assert.Contains("$_.SignatureKind -ne 'System'", script);

        // Bereitgestellte Pakete: nichts, was installiert als geschützt gilt, und keine Laufzeitbibliotheken.
        Assert.Contains("$protected -notcontains $name", script);
        Assert.Contains("'Microsoft.VCLibs.*'", script);

        var keep = AppNames.RemovalScript("Microsoft.BingWeather", keepProvisioned: true);
        Assert.DoesNotContain("Get-AppxProvisionedPackage", keep);

        Assert.Throws<InvalidDataException>(() => AppNames.RemovalScript("*", false));
        Assert.Throws<InvalidDataException>(() => AppNames.RemovalScript("x'; calc; '", false));
    }

    [Fact]
    public void Full_package_names_are_split_into_their_parts()
    {
        Assert.True(AppNames.TryParseFullName("Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe", out var name, out var version, out var architecture, out var resource, out var publisher));
        Assert.Equal(("Microsoft.WindowsNotepad", "11.2607.14.0", "x64", "", "8wekyb3d8bbwe"), (name, version, architecture, resource, publisher));
        Assert.True(AppNames.TryParseFullName("Clipchamp.Clipchamp_4.7.10020.0_neutral_~_yxz26nhyzhsrt", out _, out _, out _, out resource, out _));
        Assert.Equal("~", resource);
        Assert.False(AppNames.TryParseFullName("NotAPackage", out _, out _, out _, out _, out _));
    }

    [Fact]
    public async Task Apps_are_stored_with_the_scan_and_kept_when_an_older_runner_does_not_report_them()
    {
        var computers = new ComputerService(_db, new SimulatedHostProbe());
        var computer = (await computers.ImportAsync("PC-APPS")).Single();
        var store = new InventoryStore(_db);
        var data = new InventoryData { ComputerName = "PC-APPS" };
        data.Apps = [new AppItem { Name = "Microsoft.BingWeather", Version = "4.53", Publisher = "Microsoft Corporation", Provisioned = true }, new AppItem { Name = "MSTeams", DisplayName = "Microsoft Teams" }];
        await store.SaveAsync(computer.Id, data);

        var stored = (await computers.GetDetailAsync(computer.Id))!;
        Assert.Equal(["Microsoft.BingWeather", "MSTeams"], stored.Apps.Select(a => a.Name).Order());
        Assert.True(stored.Apps.Single(a => a.Name == "Microsoft.BingWeather").Provisioned);

        data.Apps = null;
        await store.SaveAsync(computer.Id, data);
        Assert.Equal(2, (await computers.GetDetailAsync(computer.Id))!.Apps.Count);

        data.Apps = [];
        await store.SaveAsync(computer.Id, data);
        Assert.Empty((await computers.GetDetailAsync(computer.Id))!.Apps);
    }

    [Fact]
    public void A_collection_rule_matches_package_or_display_name()
    {
        var computer = new Computer { Name = "PC", Apps = [new ComputerApp { Name = "MSTeams", DisplayName = "Microsoft Teams" }, new ComputerApp { Name = "Microsoft.BingWeather" }] };
        bool Matches(RuleOperator op, string value) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.App, Operator = op, Value = value }, computer);

        Assert.True(Matches(RuleOperator.Contains, "teams"));
        Assert.True(Matches(RuleOperator.Equals, "Microsoft.BingWeather"));
        Assert.True(Matches(RuleOperator.NotContains, "Xbox"));
        Assert.False(Matches(RuleOperator.NotContains, "Weather"));
    }
}
