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

        // Gleicher Kurzname in zwei Domänen: der genaue Name trifft, der Kurzname allein ist mehrdeutig.
        var twin = (await computers.ImportAsync("pc-02.other.example"))[0].Id;
        result = await computers.ImportAssetsAsync("Name;Owner\npc-02.other.example;Einkauf\npc-02;Wer\n");
        Assert.Equal(["pc-02.other.example"], result.Updated);
        Assert.Contains("pc-02.corp.example", Assert.Single(result.Errors));
        Assert.Equal("Einkauf", (await computers.GetAsync(twin))!.Owner);
        Assert.Equal("Lager", (await computers.GetAsync(ids[1]))!.Owner);

        // Deutsche Überschriften werden unabhängig von der Sprache des Aufrufers erkannt.
        var german = await new ComputerService(db, new SimulatedHostProbe(), new GermanHeaders()).ImportAssetsAsync("Name;Zuständig;Standort\npc-01;Technik;Werk 1\n");
        Assert.Equal(["pc-01"], german.Updated);
        Assert.Equal(("Technik", "Werk 1"), ((await computers.GetAsync(ids[0]))!.Owner, (await computers.GetAsync(ids[0]))!.Location));

        await Assert.ThrowsAsync<InvalidOperationException>(() => computers.ImportAssetsAsync("Owner;Location\nA;B"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => computers.ImportAssetsAsync("Name;Operating system\npc-01;x"));
    }

    [Fact]
    public async Task The_export_can_be_edited_and_imported_again()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var id = (await computers.ImportAsync("pc-01"))[0].Id;
        // Die Notiz beginnt mit einem Minus: der Export schützt sie vor Excel, der Import nimmt den Schutz wieder weg.
        await computers.UpdateAssetAsync(id, new AssetData("Vertrieb", "Halle 2", "-5 Grad", null, null, new DateOnly(2027, 3, 1), "Kiosk", "Zeile 1; mit Semikolon"));

        var csv = await new ReportService(db).ComputersCsvAsync();
        var result = await computers.ImportAssetsAsync(csv.Replace("Halle 2", "Halle 3"));

        Assert.Equal(["pc-01"], result.Updated);
        Assert.Empty(result.Errors);
        var computer = (await computers.GetAsync(id))!;
        Assert.Equal(("Vertrieb", "Halle 3", new DateOnly(2027, 3, 1), "Kiosk", "Zeile 1; mit Semikolon"), (computer.Owner, computer.Location, computer.WarrantyUntil, computer.Tags, computer.Notes));
        Assert.Equal("-5 Grad", computer.CostCenter);
    }

    [Fact]
    public async Task The_primary_user_is_whoever_had_an_interactive_session_on_the_most_days_and_never_a_service_account()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;
        await using (var context = db.CreateDbContext())
        {
            context.Credentials.Add(new Credential { Name = "Scan", Username = @"CORP\svc_scan", ProtectedPassword = "x" });
            await context.SaveChangesAsync();
        }
        InventoryData Scan(params string[] interactive) => new()
        {
            ComputerName = "PC-01", InteractiveUsers = [.. interactive],
            UserProfiles =
            [
                new UserProfileState { Sid = "S-1-5-21-1-2-3-1001", Name = @"CORP\anna", LastUseUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc) },
                // Das Konto des Servers hat ein Profil und es ist geladen, weil der Scan gerade läuft.
                new UserProfileState { Sid = "S-1-5-21-1-2-3-1002", Name = @"CORP\svc_scan", LastUseUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc), Loaded = true },
            ],
        };

        // Niemand interaktiv angemeldet: kein Hauptbenutzer, auch wenn Profile vorhanden sind.
        await store.SaveAsync(id, Scan());
        var detail = (await computers.GetDetailAsync(id))!;
        Assert.Null(detail.PrimaryUser);
        Assert.Equal(2, detail.UserProfiles.Count);

        // Das Dienstkonto zählt auch dann nicht, wenn es einmal interaktiv angemeldet war.
        await store.SaveAsync(id, Scan(@"corp\SVC_SCAN"));
        Assert.Null((await computers.GetAsync(id))!.PrimaryUser);

        // Beobachtungen aus früheren Tagen: Anna an drei Tagen, Bernd an zweien; Bernds ältere Tage zählen nicht mehr.
        await using (var context = db.CreateDbContext())
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            context.UserSightings.AddRange(Enumerable.Range(1, 3).Select(d => new ComputerUserSighting { ComputerId = id, UserName = @"CORP\anna", Day = today.AddDays(-d) }));
            context.UserSightings.AddRange(Enumerable.Range(40, 5).Select(d => new ComputerUserSighting { ComputerId = id, UserName = @"CORP\bernd", Day = today.AddDays(-d) }));
            context.UserSightings.Add(new ComputerUserSighting { ComputerId = id, UserName = @"CORP\bernd", Day = today.AddDays(-1) });
            await context.SaveChangesAsync();
        }
        // Zwei Scans am selben Tag zählen als ein Tag; auch die Remotedesktop-Sitzung zählt.
        await store.SaveAsync(id, Scan(@"CORP\bernd", @"CORP\svc_scan"));
        await store.SaveAsync(id, Scan(@"CORP\bernd"));
        Assert.Equal(@"CORP\anna", (await computers.GetAsync(id))!.PrimaryUser);

        // Ein älterer Runner meldet nur den Benutzer an der Konsole und keine Profilnamen: die Profile bleiben stehen.
        await store.SaveAsync(id, new InventoryData { ComputerName = "PC-01", LoggedOnUser = @"CORP\anna", UserProfiles = [new UserProfileState { Sid = "S-1-5-21-1-2-3-1001" }] });
        detail = (await computers.GetDetailAsync(id))!;
        Assert.Equal(2, detail.UserProfiles.Count);
        Assert.Equal(@"CORP\anna", detail.PrimaryUser);
    }

    [Theory]
    [InlineData(@"CORP\svc_scan", "svc_scan")]
    [InlineData("svc_scan@corp.example", "svc_scan")]
    [InlineData(@".\Administrator", "Administrator")]
    [InlineData("anna", "anna")]
    public void Account_names_are_compared_without_domain(string user, string expected) =>
        Assert.Equal(expected, InventoryStore.AccountName(user));

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

    /// <summary>Übersetzt nur unter deutscher Oberflächensprache, wie die echte Ressourcendatei.</summary>
    private sealed class GermanHeaders : Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>
    {
        private static readonly Dictionary<string, string> Texts = new() { ["Owner"] = "Zuständig", ["Location"] = "Standort" };

        public Microsoft.Extensions.Localization.LocalizedString this[string name] =>
            new(name, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" && Texts.TryGetValue(name, out var text) ? text : name);

        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] => new(name, string.Format(this[name].Value, arguments));

        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
