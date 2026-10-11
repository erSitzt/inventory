using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class PatchTests
{
    private static UpdateItem Cumulative => new()
    {
        Title = "2026-10 Cumulative Update for Windows 11 (KB5066835)", KbIds = ["KB5066835"], Classification = "Security Updates", Severity = "Critical", SizeBytes = 700 << 20,
    };

    private static UpdateItem Definition => new() { Title = "Security Intelligence Update (KB2267602)", KbIds = ["KB2267602"], Classification = "Definition Updates" };

    private static InventoryData Scan(params UpdateItem[] updates) => new()
    {
        ComputerName = "PC",
        Security = new SecurityStatus { UpdatePendingDetails = [.. updates], UpdatePending = updates.Select(u => u.Title).ToList() },
    };

    [Fact]
    public async Task Pending_updates_are_stored_grouped_across_computers_and_exported()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var reports = new ReportService(db);
        var ids = (await computers.ImportAsync("pc-01\npc-02\npc-03")).Select(c => c.Id).ToList();

        await store.SaveAsync(ids[0], Scan(Cumulative, Definition));
        // Ein älterer Runner meldet nur Titel: KB-Nummer und Sicherheitsbezug kommen aus dem Titel.
        await store.SaveAsync(ids[1], new InventoryData { ComputerName = "PC", Security = new SecurityStatus { UpdatePending = [Cumulative.Title] } });
        await store.SaveAsync(ids[2], Scan());

        var first = (await computers.GetDetailAsync(ids[0]))!;
        Assert.Equal(2, first.UpdatePendingCount);
        // Definitionsupdates sind keine Sicherheitsupdates, auch wenn "Security" im Titel steht.
        Assert.Equal(1, first.UpdatePendingSecurityCount);
        Assert.Equal(1, (await computers.GetDetailAsync(ids[1]))!.UpdatePendingSecurityCount);

        var report = await reports.PatchComplianceAsync();
        Assert.Equal(3, report.Computers.Count);
        Assert.Equal(1, report.Compliant);
        var top = report.Updates[0];
        Assert.Equal(("KB5066835", 2, true, "Critical"), (top.Key, top.Computers, top.IsSecurity, top.Severity));
        Assert.Equal(["pc-01", "pc-02"], (await reports.ComputersMissingUpdateAsync(top.Key)).Select(c => c.Name));

        var csv = await reports.PendingUpdatesCsvAsync();
        Assert.Contains("pc-01;KB5066835;", csv);
        Assert.Equal(4, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task A_scan_whose_update_agent_did_not_answer_keeps_the_previous_state()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;

        await store.SaveAsync(id, Scan(Cumulative));
        await store.SaveAsync(id, new InventoryData { ComputerName = "PC", Security = new SecurityStatus() });
        var detail = (await computers.GetDetailAsync(id))!;
        Assert.Equal(1, detail.UpdatePendingCount);
        Assert.Single(detail.PendingUpdates);

        // Eine Suche ohne Treffer ist dagegen eine Aussage.
        await store.SaveAsync(id, Scan());
        detail = (await computers.GetDetailAsync(id))!;
        Assert.Equal(0, detail.UpdatePendingCount);
        Assert.Equal(0, detail.UpdatePendingSecurityCount);
        Assert.Empty(detail.PendingUpdates);
    }

    [Fact]
    public async Task Titles_stored_before_the_details_existed_are_turned_into_entries_once()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;
        await using (var context = db.CreateDbContext())
        {
            var computer = await context.Computers.FindAsync(id);
            computer!.UpdatePendingCount = 2;
            computer.UpdatePending = Cumulative.Title + "\n" + "Update for Microsoft Defender Antivirus antimalware platform";
            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await store.BackfillPendingUpdatesAsync());
        Assert.Equal(0, await store.BackfillPendingUpdatesAsync());

        var detail = (await computers.GetDetailAsync(id))!;
        Assert.Equal(1, detail.UpdatePendingSecurityCount);
        Assert.Equal(["KB5066835", null], detail.PendingUpdates.OrderBy(u => u.Title).Select(u => u.KbIds));
    }

    [Fact]
    public void Rules_match_hotfixes_pending_updates_and_the_security_count()
    {
        static bool Matches(RuleField field, RuleOperator op, string value, Computer computer) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, computer);

        var computer = new Computer
        {
            Name = "pc", UpdatePendingSecurityCount = 2,
            Hotfixes = [new ComputerHotfix { HotfixId = "KB5044284" }],
            PendingUpdates = [new ComputerPendingUpdate { Title = "2026-10 Cumulative Update", KbIds = "KB5066835, KB5066836" }],
        };
        Assert.True(Matches(RuleField.Hotfix, RuleOperator.Equals, "kb5044284", computer));
        Assert.True(Matches(RuleField.Hotfix, RuleOperator.NotEquals, "KB5066835", computer));
        Assert.False(Matches(RuleField.Hotfix, RuleOperator.NotEquals, "KB5044284", computer));
        Assert.True(Matches(RuleField.PendingUpdate, RuleOperator.Equals, "KB5066836", computer));
        Assert.True(Matches(RuleField.PendingUpdate, RuleOperator.Contains, "cumulative", computer));
        Assert.True(Matches(RuleField.PendingUpdate, RuleOperator.NotContains, "Defender", computer));
        Assert.True(Matches(RuleField.PendingSecurityUpdates, RuleOperator.GreaterOrEqual, "1", computer));
        Assert.False(Matches(RuleField.PendingSecurityUpdates, RuleOperator.GreaterOrEqual, "1", new Computer { Name = "x" }));
    }

    [Theory]
    [InlineData("Security Updates", null, "x", true)]
    [InlineData("Updates", "Important", "x", true)]
    [InlineData("Definition Updates", null, "Security Intelligence Update", false)]
    [InlineData(null, null, "2026-10 Kumulatives Update für Windows 11 (KB5066835)", true)]
    [InlineData(null, null, "Update for Microsoft Defender Antivirus antimalware platform", false)]
    public void Security_relevance_follows_classification_severity_or_the_title(string? classification, string? severity, string title, bool expected) =>
        Assert.Equal(expected, PendingUpdates.IsSecurity(classification, severity, title));
}
