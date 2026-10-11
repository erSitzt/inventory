using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class AssetTests
{
    [Fact]
    public async Task Asset_data_is_stored_normalized_and_imported_from_a_table()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var ids = (await computers.ImportAsync("pc-01\npc-02.corp.example")).Select(c => c.Id).ToList();

        await computers.UpdateAssetAsync(ids[0], new AssetData(" Vertrieb ", "Halle 2", "4711", "INV-1", new DateOnly(2024, 3, 1), new DateOnly(2027, 3, 1), "Kiosk; Leihgerät, kiosk", "  "));
        var first = (await computers.GetAsync(ids[0]))!;
        Assert.Equal("Vertrieb", first.Owner);
        Assert.Equal("Kiosk, Leihgerät", first.Tags);
        Assert.Null(first.Notes);

        // Kopfzeile englisch, Semikolon; leere Zelle = unverändert, "-" = löschen, unbekannte Spalte wird ignoriert.
        var result = await computers.ImportAssetsAsync(
            "Name;Owner;Location;Warranty until;Tags;Operating system\r\n" +
            "PC-01;;-;31.03.2028;\"Kiosk, Messe\";Windows 11\r\n" +
            "pc-02;Lager;Halle 1;2026-12-31;;\r\n" +
            "pc-03;x;;;;\r\n" +
            "pc-02.corp.example;;;morgen;;\r\n");
        Assert.Equal(["pc-01", "pc-02.corp.example"], result.Updated);
        Assert.Equal(["pc-03"], result.UnknownNames);
        Assert.Contains("morgen", Assert.Single(result.Errors));

        first = (await computers.GetAsync(ids[0]))!;
        Assert.Equal(("Vertrieb", null, new DateOnly(2028, 3, 31), "Kiosk, Messe", "4711"), (first.Owner, first.Location, first.WarrantyUntil, first.Tags, first.CostCenter));
        var second = (await computers.GetAsync(ids[1]))!;
        Assert.Equal(("Lager", "Halle 1", new DateOnly(2026, 12, 31)), (second.Owner, second.Location, second.WarrantyUntil));

        await Assert.ThrowsAsync<InvalidOperationException>(() => computers.ImportAssetsAsync("Owner;Location\nA;B"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => computers.ImportAssetsAsync("Name;Operating system\npc-01;x"));
    }

    [Fact]
    public async Task The_export_can_be_edited_and_imported_again()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var id = (await computers.ImportAsync("pc-01"))[0].Id;
        await computers.UpdateAssetAsync(id, new AssetData("Vertrieb", "Halle 2", null, null, null, new DateOnly(2027, 3, 1), "Kiosk", "Zeile 1; mit Semikolon"));

        var csv = await new ReportService(db).ComputersCsvAsync();
        var result = await computers.ImportAssetsAsync(csv.Replace("Halle 2", "Halle 3"));

        Assert.Equal(["pc-01"], result.Updated);
        Assert.Empty(result.Errors);
        var computer = (await computers.GetAsync(id))!;
        Assert.Equal(("Vertrieb", "Halle 3", new DateOnly(2027, 3, 1), "Kiosk", "Zeile 1; mit Semikolon"), (computer.Owner, computer.Location, computer.WarrantyUntil, computer.Tags, computer.Notes));
    }

    [Fact]
    public async Task The_primary_user_is_whoever_was_logged_on_on_the_most_days()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;
        InventoryData Scan(string? user) => new()
        {
            ComputerName = "PC-01", LoggedOnUser = user,
            UserProfiles =
            [
                new UserProfileState { Sid = "S-1-5-21-1-2-3-1001", Name = @"CORP\anna", LastUseUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc) },
                new UserProfileState { Sid = "S-1-5-21-1-2-3-1002", Name = @"CORP\admin", LastUseUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc), Loaded = true },
            ],
        };

        // Niemand angemeldet: das zuletzt genutzte Profil.
        await store.SaveAsync(id, Scan(null));
        var detail = (await computers.GetDetailAsync(id))!;
        Assert.Equal(@"CORP\admin", detail.PrimaryUser);
        Assert.Equal(2, detail.UserProfiles.Count);

        // Beobachtungen aus früheren Tagen: Anna an drei Tagen, der Administrator an einem.
        await using (var context = db.CreateDbContext())
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            context.UserSightings.AddRange(Enumerable.Range(1, 3).Select(d => new ComputerUserSighting { ComputerId = id, UserName = @"CORP\anna", Day = today.AddDays(-d) }));
            // Zu alt, zählt nicht mehr.
            context.UserSightings.AddRange(Enumerable.Range(40, 5).Select(d => new ComputerUserSighting { ComputerId = id, UserName = @"CORP\admin", Day = today.AddDays(-d) }));
            await context.SaveChangesAsync();
        }
        await store.SaveAsync(id, Scan(@"CORP\admin"));
        await store.SaveAsync(id, Scan(@"CORP\admin"));
        Assert.Equal(@"CORP\anna", (await computers.GetAsync(id))!.PrimaryUser);

        // Ein älterer Runner ohne Profilnamen lässt Profile und Hauptbenutzer stehen.
        await store.SaveAsync(id, new InventoryData { ComputerName = "PC-01", UserProfiles = [new UserProfileState { Sid = "S-1-5-21-1-2-3-1001" }] });
        detail = (await computers.GetDetailAsync(id))!;
        Assert.Equal(2, detail.UserProfiles.Count);
        Assert.Equal(@"CORP\anna", detail.PrimaryUser);
    }

    [Fact]
    public void Rules_match_tags_asset_fields_and_remaining_warranty()
    {
        static bool Matches(RuleField field, RuleOperator op, string value, Computer computer) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, computer);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var computer = new Computer { Name = "pc", Tags = "Kiosk, Leihgerät", Location = "Halle 2", Owner = "Vertrieb", PrimaryUser = @"CORP\anna", WarrantyUntil = today.AddDays(30) };
        var expired = new Computer { Name = "old", WarrantyUntil = today.AddDays(-10) };

        Assert.True(Matches(RuleField.Tag, RuleOperator.Equals, "kiosk", computer));
        Assert.False(Matches(RuleField.Tag, RuleOperator.Equals, "Kio", computer));
        Assert.True(Matches(RuleField.Tag, RuleOperator.NotEquals, "Server", computer));
        Assert.True(Matches(RuleField.Location, RuleOperator.StartsWith, "Halle", computer));
        Assert.True(Matches(RuleField.Owner, RuleOperator.Equals, "vertrieb", computer));
        Assert.True(Matches(RuleField.PrimaryUser, RuleOperator.Contains, "anna", computer));
        Assert.True(Matches(RuleField.WarrantyDaysLeft, RuleOperator.LessThan, "90", computer));
        Assert.False(Matches(RuleField.WarrantyDaysLeft, RuleOperator.LessThan, "0", computer));
        Assert.True(Matches(RuleField.WarrantyDaysLeft, RuleOperator.LessThan, "0", expired));
        // Ohne eingetragenes Datum passt nichts.
        Assert.False(Matches(RuleField.WarrantyDaysLeft, RuleOperator.LessThan, "90", new Computer { Name = "x" }));
        Assert.False(Matches(RuleField.WarrantyDaysLeft, RuleOperator.GreaterOrEqual, "0", new Computer { Name = "x" }));
    }
}
