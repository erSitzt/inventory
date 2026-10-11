using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class CollectionRuleTests
{
    private static bool Matches(RuleField field, RuleOperator op, string value, Computer computer, string? value2 = null) =>
        CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value, Value2 = value2 }, computer);

    [Fact]
    public void Wildcards_match_any_characters_with_equals_and_its_negation()
    {
        var computer = new Computer { Name = "PC-Lager-01", Model = "ThinkPad T14 (Gen 3)" };
        Assert.True(Matches(RuleField.ComputerName, RuleOperator.Equals, "pc-*-01", computer));
        Assert.False(Matches(RuleField.ComputerName, RuleOperator.Equals, "pc-*-02", computer));
        Assert.True(Matches(RuleField.ComputerName, RuleOperator.NotEquals, "srv-*", computer));
        // Zeichen mit Sonderbedeutung in regulären Ausdrücken sind hier gewöhnliche Zeichen.
        Assert.True(Matches(RuleField.Model, RuleOperator.Equals, "*(gen 3)", computer));
        Assert.False(Matches(RuleField.Model, RuleOperator.Equals, "ThinkPad.T14*", computer));
        // Ohne * bleibt "gleich" ein genauer Vergleich.
        Assert.False(Matches(RuleField.ComputerName, RuleOperator.Equals, "PC-Lager", computer));
    }

    [Fact]
    public void General_and_hardware_fields_are_evaluated_and_unknown_values_do_not_match()
    {
        var computer = new Computer
        {
            Name = "pc", Domain = "corp.example", SerialNumber = "ABC123", CpuName = "Intel Core i5", OsArchitecture = "64-Bit", IpAddress = "10.20.3.4",
            LastBootUtc = DateTime.UtcNow.AddDays(-40), LastScanUtc = DateTime.UtcNow.AddDays(-2), AgentKeyHash = "x",
            ChassisType = 10, Model = "Latitude", SecureBoot = false, TpmPresent = true, TpmReady = true, SystemDriveProtected = false,
            FirmwareType = "UEFI", Gpu = "NVIDIA T1000", BatteryHealthPercent = 61,
            Disks = [new ComputerDisk { Drive = "C:", FreeBytes = 12L << 30, SizeBytes = 256L << 30 }, new ComputerDisk { Drive = "D:", FreeBytes = 900L << 30 }],
            PhysicalDisks = [new ComputerPhysicalDisk { Model = "a", Health = "Healthy" }, new ComputerPhysicalDisk { Model = "b", Health = "Warning" }],
            Services = [new ComputerWindowsService { Name = "Spooler", DisplayName = "Druckwarteschlange", State = "Running" }, new ComputerWindowsService { Name = "WinRM", State = "Stopped" }],
        };
        var unknown = new Computer { Name = "x" };

        Assert.True(Matches(RuleField.IpAddress, RuleOperator.StartsWith, "10.20.", computer));
        Assert.True(Matches(RuleField.Domain, RuleOperator.Equals, "CORP.example", computer));
        Assert.True(Matches(RuleField.SerialNumber, RuleOperator.StartsWith, "abc", computer));
        Assert.True(Matches(RuleField.CpuName, RuleOperator.Contains, "i5", computer));
        Assert.True(Matches(RuleField.OsArchitecture, RuleOperator.StartsWith, "64", computer));
        Assert.True(Matches(RuleField.DiskFreeGb, RuleOperator.LessThan, "20", computer));
        Assert.False(Matches(RuleField.DiskFreeGb, RuleOperator.LessThan, "20", unknown));
        Assert.True(Matches(RuleField.DaysSinceBoot, RuleOperator.GreaterOrEqual, "30", computer));
        Assert.True(Matches(RuleField.ScanAgeDays, RuleOperator.LessThan, "7", computer));
        Assert.False(Matches(RuleField.ScanAgeDays, RuleOperator.GreaterOrEqual, "0", unknown));
        Assert.True(Matches(RuleField.AgentPresent, RuleOperator.Equals, "true", computer));
        Assert.True(Matches(RuleField.AgentPresent, RuleOperator.Equals, "false", unknown));

        Assert.True(Matches(RuleField.DeviceType, RuleOperator.Equals, "Laptop", computer));
        Assert.True(Matches(RuleField.DeviceType, RuleOperator.NotEquals, "Server", computer));
        Assert.False(Matches(RuleField.DeviceType, RuleOperator.NotEquals, "Server", unknown));
        Assert.True(Matches(RuleField.SecureBoot, RuleOperator.Equals, "false", computer));
        Assert.False(Matches(RuleField.SecureBoot, RuleOperator.Equals, "false", unknown));
        Assert.True(Matches(RuleField.TpmReady, RuleOperator.Equals, "true", computer));
        Assert.True(Matches(RuleField.TpmReady, RuleOperator.Equals, "false", new Computer { Name = "old", TpmPresent = false }));
        Assert.True(Matches(RuleField.SystemDriveProtected, RuleOperator.Equals, "false", computer));
        Assert.True(Matches(RuleField.FirmwareType, RuleOperator.Equals, "uefi", computer));
        Assert.True(Matches(RuleField.Gpu, RuleOperator.Contains, "nvidia", computer));
        Assert.True(Matches(RuleField.BatteryHealthPercent, RuleOperator.LessThan, "70", computer));
        Assert.True(Matches(RuleField.DiskHealthProblem, RuleOperator.Equals, "true", computer));
        Assert.False(Matches(RuleField.DiskHealthProblem, RuleOperator.Equals, "false", unknown));

        Assert.True(Matches(RuleField.Service, RuleOperator.Equals, "spooler", computer));
        Assert.True(Matches(RuleField.Service, RuleOperator.Equals, "Druckwarteschlange", computer, "Running"));
        Assert.False(Matches(RuleField.Service, RuleOperator.Equals, "Spooler", computer, "Stopped"));
        Assert.True(Matches(RuleField.Service, RuleOperator.Equals, "WinRM", computer, "Stopped"));
        Assert.True(Matches(RuleField.Service, RuleOperator.NotEquals, "Kiosk", computer));
        Assert.False(Matches(RuleField.Service, RuleOperator.NotEquals, "Spooler", computer));
        // Verneint zählt nur, ob es den Dienst gibt; ein mitgeschickter Zustand ändert daran nichts.
        Assert.False(Matches(RuleField.Service, RuleOperator.NotEquals, "Spooler", computer, "Stopped"));

        // Datenträgerzustand: ein Problem zählt immer, "kein Problem" nur bei vollständig bekannten Zuständen.
        var partly = new Computer { Name = "p", PhysicalDisks = [new ComputerPhysicalDisk { Model = "a", Health = "Healthy" }, new ComputerPhysicalDisk { Model = "b" }] };
        Assert.False(Matches(RuleField.DiskHealthProblem, RuleOperator.Equals, "false", partly));
        Assert.False(Matches(RuleField.DiskHealthProblem, RuleOperator.Equals, "true", partly));
        Assert.True(Matches(RuleField.DiskHealthProblem, RuleOperator.Equals, "false", new Computer { Name = "ok", PhysicalDisks = [new ComputerPhysicalDisk { Model = "a", Health = "Healthy" }] }));
    }

    [Fact]
    public async Task Collections_can_be_combined_and_circles_depth_and_dangling_references_are_refused()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var collections = new CollectionService(db);
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("lap-01\nlap-02\npc-01")).Select(c => c.Id).ToList();
        await store.SaveAsync(ids[0], new Inventory.Contracts.InventoryData { ComputerName = "LAP-01", Services = [new Inventory.Contracts.ServiceItem { Name = "Spooler", State = "Running" }] });

        static CollectionRule In(Collection c, bool negate = false) => new() { Field = RuleField.InCollection, Operator = negate ? RuleOperator.NotEquals : RuleOperator.Equals, Value = c.Id.ToString() };
        var pilot = await collections.SaveAsync(null, "Pilot", null, false, true, []);
        await collections.AddMembersAsync(pilot.Id, [ids[1]]);
        var laptops = await collections.SaveAsync(null, "Laptops", null, true, true, [new CollectionRule { Field = RuleField.ComputerName, Operator = RuleOperator.Equals, Value = "lap-*" }]);
        // Dynamisch in dynamisch plus eine statische Ausnahme; die Dienst-Regel braucht die Dienstliste.
        var rest = await collections.SaveAsync(null, "Laptops ohne Pilot", null, true, true, [In(laptops), In(pilot, negate: true)]);
        var printing = await collections.SaveAsync(null, "Laptops mit Spooler", null, true, true,
            [In(rest), new CollectionRule { Field = RuleField.Service, Operator = RuleOperator.Equals, Value = "Spooler", Value2 = "Running" }]);

        Assert.Equal([ids[0], ids[1]], (await collections.GetMemberIdsAsync(laptops.Id)).Order());
        Assert.Equal([ids[0]], await collections.GetMemberIdsAsync(rest.Id));
        Assert.Equal([ids[0]], await collections.GetMemberIdsAsync(printing.Id));
        var counts = (await collections.ListAsync()).ToDictionary(s => s.Collection.Name, s => s.Count);
        Assert.Equal((2, 1, 1, 1), (counts["Laptops"], counts["Laptops ohne Pilot"], counts["Laptops mit Spooler"], counts["Pilot"]));

        // Kreis: Laptops → Laptops mit Spooler → Laptops ohne Pilot → Laptops.
        await Assert.ThrowsAsync<InvalidOperationException>(() => collections.SaveAsync(laptops.Id, "Laptops", null, true, true, [In(printing)]));
        // Auf sich selbst.
        await Assert.ThrowsAsync<InvalidOperationException>(() => collections.SaveAsync(rest.Id, "Laptops ohne Pilot", null, true, true, [In(rest)]));
        // Nicht vorhanden.
        await Assert.ThrowsAsync<InvalidOperationException>(() => collections.SaveAsync(null, "Kaputt", null, true, true, [new CollectionRule { Field = RuleField.InCollection, Value = "9999" }]));
        // Tiefe: vier Verweise hintereinander sind einer zu viel.
        var level3 = await collections.SaveAsync(null, "Ebene 3", null, true, true, [In(printing)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => collections.SaveAsync(null, "Ebene 4", null, true, true, [In(level3)]));
        // Auch von unten: eine Sammlung am Ende der Kette darf nicht selbst noch tiefer verweisen.
        var extra = await collections.SaveAsync(null, "Extra", null, true, true, [new CollectionRule { Field = RuleField.ComputerName, Operator = RuleOperator.Contains, Value = "x" }]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => collections.SaveAsync(laptops.Id, "Laptops", null, true, true, [In(extra)]));
        // Die abgelehnten Änderungen haben nichts verändert.
        Assert.Equal([ids[0], ids[1]], (await collections.GetMemberIdsAsync(laptops.Id)).Order());

        // Eine Sammlung, auf die andere verweisen, lässt sich nicht löschen.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => collections.DeleteAsync(pilot.Id));
        Assert.Contains("Laptops ohne Pilot", error.Message);
        await collections.DeleteAsync(level3.Id);
    }

    [Fact]
    public void A_rule_on_a_missing_collection_matches_nobody_even_when_negated()
    {
        var context = new RuleContext([]);
        var computer = new Computer { Id = 1, Name = "pc" };
        Assert.False(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.InCollection, Operator = RuleOperator.Equals, Value = "5" }, computer, context));
        Assert.False(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.InCollection, Operator = RuleOperator.NotEquals, Value = "5" }, computer, context));
    }
}
