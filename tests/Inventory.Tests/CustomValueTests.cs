using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>Eigene Inventarfelder: Normalisieren, Skriptausgabe, Speichern mit Verlauf, Scan, Sammlungsregeln, Berichte.</summary>
public class CustomValueTests
{
    private sealed class FieldCatalog(params CustomFieldInfo[] fields) : ICustomFieldCatalog
    {
        public List<CustomScriptInfo> ScriptList { get; } = [];
        public IReadOnlyList<CustomFieldInfo> Fields => fields;
        public IReadOnlyList<CustomScriptInfo> Scripts => ScriptList;
    }

    private static readonly CustomFieldInfo Version = new("contoso.lob.version", "LOB version", CustomFieldKind.Text, "Contoso");
    private static readonly CustomFieldInfo Seats = new("contoso.lob.seats", "Seats", CustomFieldKind.Number, "Contoso");
    private static readonly CustomFieldInfo Expires = new("contoso.lob.expires", "Licence expires", CustomFieldKind.Date, "Contoso");
    private static readonly CustomFieldInfo Active = new("contoso.lob.active", "Active", CustomFieldKind.Bool, "Contoso");

    [Theory]
    [InlineData(CustomFieldKind.Text, "  1.2.3 ", "1.2.3")]
    [InlineData(CustomFieldKind.Number, "42", "42")]
    [InlineData(CustomFieldKind.Number, "1.50", "1.50")]
    [InlineData(CustomFieldKind.Number, "-3e2", "-300")]
    [InlineData(CustomFieldKind.Date, "2026-11-02T10:15:00", "2026-11-02")]
    [InlineData(CustomFieldKind.Date, "/Date(1767225600000)/", "2026-01-01")]
    [InlineData(CustomFieldKind.Bool, "True", "true")]
    [InlineData(CustomFieldKind.Bool, "0", "false")]
    public void Values_are_normalized_by_type(CustomFieldKind kind, string input, string expected) =>
        Assert.Equal(expected, CustomValues.Normalize(kind, input));

    [Theory]
    [InlineData(CustomFieldKind.Number, "many")]
    [InlineData(CustomFieldKind.Date, "soon")]
    [InlineData(CustomFieldKind.Bool, "maybe")]
    public void Values_that_do_not_fit_the_type_are_rejected(CustomFieldKind kind, string input) =>
        Assert.Throws<FormatException>(() => CustomValues.Normalize(kind, input));

    [Fact]
    public void Script_output_is_read_from_the_last_json_object_that_starts_a_line()
    {
        // Davor Meldungen mit geschweiften Klammern (etwa das Echo eines Skripts), danach mehrzeiliges JSON mit innerem Objekt.
        var output = "Running @{ version = 1 }\nWARNING: {not json}\n{\n  \"version\": \"2.1\",\n  \"seats\": 12,\n  \"active\": true,\n  \"extra\": { \"a\": 1 },\n  \"gone\": null\n}\n";
        var values = CustomValues.ParseOutput(output);
        Assert.Equal("2.1", values["version"]);
        Assert.Equal("12", values["seats"]);
        Assert.Equal("true", values["active"]);
        Assert.Equal("{ \"a\": 1 }", values["extra"]);
        Assert.Null(values["gone"]);

        Assert.Equal("3", CustomValues.ParseOutput("{\"version\":\"3\"}")["version"]);
        Assert.Throws<FormatException>(() => CustomValues.ParseOutput("nothing here"));
        Assert.Throws<FormatException>(() => CustomValues.ParseOutput("{ broken"));
        Assert.Throws<FormatException>(() => CustomValues.ParseOutput(null));
    }

    [Fact]
    public async Task Values_are_stored_and_every_change_lands_in_the_history()
    {
        using var db = new TestDb();
        var computer = (await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01"))[0];
        var service = new CustomValueService(db, new FieldCatalog(Version, Seats, Expires, Active));

        await service.SetAsync(computer.Id, "contoso.lob.version", "1.0");
        await service.SetAsync(computer.Id, "contoso.lob.version", "1.0");
        await service.SetAsync(computer.Id, "CONTOSO.LOB.VERSION", "1.1");
        await service.SetAsync(computer.Id, "contoso.lob.seats", "5");
        await service.SetAsync(computer.Id, "contoso.lob.seats", null);
        Assert.Equal(["contoso.lob.version=1.1"], (await service.GetAsync(computer.Id)).Select(v => $"{v.Key}={v.Value}"));

        await using (var context = db.CreateDbContext())
        {
            var changes = await context.InventoryChanges.Where(c => c.ComputerId == computer.Id).OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(["Added LOB version", "Changed LOB version", "Added Seats", "Removed Seats"], changes.Select(c => $"{c.Kind} {c.Item}"));
            Assert.All(changes, c => Assert.Equal("Contoso", c.Category));
            Assert.Equal(("1.0", "1.1"), (changes[1].OldValue, changes[1].NewValue));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync(computer.Id, "contoso.lob.unknown", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync(computer.Id, "contoso.lob.seats", "lots"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetAsync(999, "contoso.lob.version", "1"));

        // Ohne strict werden unbrauchbare Einträge gemeldet, die übrigen gespeichert.
        var (stored, warnings) = await service.SetManyAsync(computer.Id, new Dictionary<string, string?>
        {
            ["contoso.lob.active"] = "yes", ["contoso.lob.expires"] = "never", ["other.field"] = "1",
        });
        Assert.Equal(1, stored);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public async Task A_scan_runs_the_scripts_and_a_broken_output_keeps_the_previous_values()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var computer = (await computers.ImportAsync("pc-01"))[0];
        var fields = new FieldCatalog(Version, Seats);
        var custom = new CustomValueService(db, fields);
        var jobs = new RemoteJobService([new SimulatedTransport(TransportKind.SmbService)], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db));
        var scans = new ScanService(jobs, new InventoryStore(db), custom: custom);

        // Kurze Schlüssel ohne "contoso.lob." genügen; die Simulation liefert die Zeile hinter "# simulate:".
        fields.ScriptList.Add(new CustomScriptInfo("contoso.lob/version", "version", "# simulate: {\"version\":\"4.2\",\"seats\":\"12\"}\nGet-Something", 60, "contoso.lob.", "Contoso"));
        Assert.True((await scans.ScanAsync(computer.Id)).Success);
        Assert.Equal(["contoso.lob.seats=12", "contoso.lob.version=4.2"], (await custom.GetAsync(computer.Id)).Select(v => $"{v.Key}={v.Value}").Order());
        var run = Assert.Single(await custom.RunsAsync(computer.Id));
        Assert.Equal(("contoso.lob/version", (string?)null), (run.Script, run.Error));

        fields.ScriptList[0] = fields.ScriptList[0] with { Script = "# simulate: this is not json" };
        Assert.True((await scans.ScanAsync(computer.Id)).Success);
        Assert.Contains("contoso.lob.version=4.2", (await custom.GetAsync(computer.Id)).Select(v => $"{v.Key}={v.Value}"));
        Assert.Contains("no JSON", Assert.Single(await custom.RunsAsync(computer.Id)).Error);

        // Ein fehlgeschlagener Schritt (die Simulation lässt Namen mit "fail" scheitern) wird vermerkt, die Werte bleiben.
        fields.ScriptList[0] = fields.ScriptList[0] with { Name = "fail" };
        Assert.True((await scans.ScanAsync(computer.Id)).Success);
        Assert.NotNull(Assert.Single(await custom.RunsAsync(computer.Id)).Error);
        Assert.Equal(2, (await custom.GetAsync(computer.Id)).Count);
    }

    [Fact]
    public async Task Collection_rules_and_reports_use_custom_fields()
    {
        using var db = new TestDb();
        var added = await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01 pc-02 pc-03");
        var custom = new CustomValueService(db, new FieldCatalog(Version, Seats));
        await custom.SetAsync(added[0].Id, "contoso.lob.version", "4.2");
        await custom.SetAsync(added[1].Id, "contoso.lob.version", "3.9");
        await custom.SetAsync(added[0].Id, "contoso.lob.seats", "12");

        var collections = new CollectionService(db);
        var outdated = await collections.SaveAsync(null, "Old LOB", null, isDynamic: true, matchAll: true,
            [new CollectionRule { Field = RuleField.CustomField, Value2 = "contoso.lob.version", Operator = RuleOperator.LessThan, Value = "4.0" }]);
        Assert.Equal([added[1].Id], await collections.GetMemberIdsAsync(outdated.Id));
        Assert.True(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.CustomField, Value2 = "contoso.lob.version", Operator = RuleOperator.Equals, Value = "4.*" },
            new Computer { Name = "x", CustomValues = [new ComputerCustomValue { Key = "CONTOSO.LOB.VERSION", Value = "4.2" }] }));

        var reports = new ReportService(db, customValues: custom);
        var table = await reports.TableAsync(new ReportQuery(ReportSource.Computers, Columns: ["name", "custom:contoso.lob.version", "custom:contoso.lob.seats"]));
        Assert.Equal(["Name", "Contoso: LOB version", "Contoso: Seats"], table.Columns.Select(c => c.Title));
        Assert.True(table.Columns[2].Numeric);
        Assert.Equal(["4.2", "12"], table.Rows.Single(r => r[0] == "pc-01")[1..]);
        Assert.Equal([null, null], table.Rows.Single(r => r[0] == "pc-03")[1..]);
    }
}
