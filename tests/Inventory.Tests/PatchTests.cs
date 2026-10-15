using Inventory.Data;
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
        Assert.Equal(["pc-01", "pc-02"], top.ComputerNames);
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
    public async Task The_pending_updates_package_restarts_only_when_needed_and_is_reused_per_kind()
    {
        using var db = new TestDb();
        var repo = Path.Combine(Path.GetTempPath(), "inv-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var packages = new PackageService(db, new PackageRepository(repo));
            var (all, created) = await packages.EnsurePendingUpdatesPackageAsync(securityOnly: false, approvedBy: "admin");
            Assert.True(created);
            Assert.True((await packages.GetAsync(all.Id))!.IsApproved);
            var (security, _) = await packages.EnsurePendingUpdatesPackageAsync(securityOnly: true);
            Assert.NotEqual(all.Id, security.Id);
            Assert.Equal((all.Id, false), await packages.EnsurePendingUpdatesPackageAsync(securityOnly: false) is var (again, againCreated) ? (again.Id, againCreated) : default);

            var steps = (await packages.GetAsync(security.Id))!.Steps.OrderBy(s => s.Order).Select(s => s.Definition).ToList();
            Assert.Equal([StepType.WindowsUpdate, StepType.Reboot], steps.Select(s => s.Type));
            Assert.True(steps[0].UpdateSecurityOnly);
            Assert.Empty(steps[0].UpdateKbIds);
            Assert.True(steps[1].Conditions!.RebootPending);
            Assert.Equal(300, steps[1].Seconds);
        }
        finally
        {
            if (Directory.Exists(repo))
                Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task An_update_package_installs_exactly_the_given_kb_numbers_and_is_reused()
    {
        using var db = new TestDb();
        var repo = Path.Combine(Path.GetTempPath(), "inv-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var packages = new PackageService(db, new PackageRepository(repo));
            var (package, created) = await packages.EnsureUpdatePackageAsync("KB5066835, KB5066836", approvedBy: "admin");
            Assert.True(created);
            var (again, createdAgain) = await packages.EnsureUpdatePackageAsync("kb5066835,5066836");
            Assert.False(createdAgain);
            Assert.Equal(package.Id, again.Id);

            // Ändert jemand den Schritt, installiert das Paket etwas anderes und wird für diese Updates nicht mehr genommen.
            var edited = (await packages.GetAsync(package.Id))!.Steps[0];
            edited.Definition.UpdateKbIds = ["KB1234567"];
            await packages.SaveStepAsync(package.Id, edited.Id, edited.Definition, null);
            var (fresh, createdFresh) = await packages.EnsureUpdatePackageAsync("KB5066835, KB5066836");
            Assert.True(createdFresh);
            Assert.NotEqual(package.Id, fresh.Id);
            package = fresh;

            var step = Assert.Single((await packages.GetAsync(package.Id))!.Steps).Definition;
            Assert.Equal(StepType.WindowsUpdate, step.Type);
            Assert.Equal(["KB5066835", "KB5066836"], step.UpdateKbIds);
            Assert.Equal(7200, step.TimeoutSeconds);
            // Ohne KB-Nummer gibt es nichts, woran sich das Update gezielt auswählen ließe.
            await Assert.ThrowsAsync<InvalidOperationException>(() => packages.EnsureUpdatePackageAsync("Windows 11, version 26H2"));
        }
        finally
        {
            if (Directory.Exists(repo))
                Directory.Delete(repo, true);
        }
    }

    [Fact]
    public void The_update_step_and_the_restart_condition_survive_the_manifest_round_trip()
    {
        var step = new JobStep
        {
            Type = StepType.WindowsUpdate, UpdateSecurityOnly = true, UpdateIncludeDrivers = true, UpdateMaxCount = 5, UpdateKbIds = ["KB5066835"],
            Conditions = new StepConditions { RebootPending = true },
        };
        Assert.False(step.Conditions.IsEmpty);
        var json = JobJson.Serialize(step);
        Assert.Contains("\"WindowsUpdate\"", json);
        var copy = JobJson.Deserialize<JobStep>(json)!;
        Assert.Equal((StepType.WindowsUpdate, true, true, 5, true), (copy.Type, copy.UpdateSecurityOnly, copy.UpdateIncludeDrivers, copy.UpdateMaxCount, copy.Conditions!.RebootPending));
        Assert.Equal(["KB5066835"], copy.UpdateKbIds);
        Assert.Equal(9, (int)StepType.WindowsUpdate);
    }

    [Theory]
    [InlineData("KB5066835, 5044284; kb5066835", "KB5066835,KB5044284")]
    [InlineData("Windows 11, version 26H2", "")]
    [InlineData(null, "")]
    public void Kb_numbers_are_read_from_free_text(string? text, string expected) =>
        Assert.Equal(expected, string.Join(",", PendingUpdates.NormalizeKbIds(text)));

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
    [InlineData(null, null, "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602", false)]
    public void Security_relevance_follows_classification_severity_or_the_title(string? classification, string? severity, string title, bool expected) =>
        Assert.Equal(expected, PendingUpdates.IsSecurity(classification, severity, title));
}
