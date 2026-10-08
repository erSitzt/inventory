using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class InventoryTests
{
    [Theory]
    [InlineData("1.9", "1.10", -1)]
    [InlineData("141.0.7390.55", "141.0.7390.108", -1)]
    [InlineData("24.08", "24.8.0", 0)]
    [InlineData("8.7", "8.6.9", 1)]
    [InlineData(null, "1", -1)]
    public void CompareVersions_is_numeric_per_segment(string? left, string right, int expected)
    {
        Assert.Equal(expected, CollectionEvaluator.CompareVersions(left, right));
    }

    [Fact]
    public void Software_rules_cover_installed_outdated_and_missing()
    {
        var computer = new Computer
        {
            Name = "pc-01",
            Software = [new ComputerSoftware { Name = "Google Chrome", Version = "139.0.1" }, new ComputerSoftware { Name = "7-Zip 24.08 (x64)", Version = "24.08" }],
        };
        static CollectionRule Rule(RuleOperator op, string value, string? below = null) =>
            new() { Field = RuleField.Software, Operator = op, Value = value, Value2 = below };

        Assert.True(CollectionEvaluator.Matches(Rule(RuleOperator.Contains, "chrome"), computer));
        Assert.True(CollectionEvaluator.Matches(Rule(RuleOperator.Contains, "chrome", "141"), computer));
        Assert.False(CollectionEvaluator.Matches(Rule(RuleOperator.Contains, "chrome", "139"), computer));
        Assert.False(CollectionEvaluator.Matches(Rule(RuleOperator.NotContains, "7-zip"), computer));
        Assert.True(CollectionEvaluator.Matches(Rule(RuleOperator.NotContains, "firefox"), computer));
    }

    [Fact]
    public void Numeric_rules_do_not_match_unscanned_computers()
    {
        var rule = new CollectionRule { Field = RuleField.MemoryGb, Operator = RuleOperator.LessThan, Value = "16" };

        Assert.False(CollectionEvaluator.Matches(rule, new Computer { Name = "neu" }));
        Assert.True(CollectionEvaluator.Matches(rule, new Computer { Name = "klein", MemoryBytes = 8L * 1024 * 1024 * 1024 }));
        Assert.False(CollectionEvaluator.Matches(rule, new Computer { Name = "gross", MemoryBytes = 32L * 1024 * 1024 * 1024 }));
    }

    [Fact]
    public async Task Scan_stores_inventory_replaces_previous_and_feeds_dynamic_collections()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var transport = new InventoryTransport();
        var jobs = new RemoteJobService([transport], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db));
        var scans = new ScanService(jobs, new InventoryStore(db));
        var collections = new CollectionService(db);
        var added = await computers.ImportAsync("pc-01 pc-02");
        var outdated = await collections.SaveAsync(null, "Chrome veraltet", null, isDynamic: true, matchAll: true,
            [new CollectionRule { Field = RuleField.Software, Operator = RuleOperator.Contains, Value = "Chrome", Value2 = "141" }]);

        transport.ChromeVersion = "139.0";
        var outcomes = await scans.ScanAsync(added.Select(c => c.Id));
        Assert.All(outcomes, o => Assert.True(o.Success));
        Assert.Equal(2, (await collections.GetMemberIdsAsync(outdated.Id)).Count);

        transport.ChromeVersion = "141.0";
        await scans.ScanAsync(added[0].Id);
        var detail = await computers.GetDetailAsync(added[0].Id);
        Assert.Equal("141.0", Assert.Single(detail!.Software).Version);
        Assert.Equal("Testmodell", detail.Model);
        Assert.NotNull(detail.LastScanUtc);
        Assert.Equal([added[1].Id], await collections.GetMemberIdsAsync(outdated.Id));
        Assert.Equal(1, (await collections.ListAsync()).Single().Count);
    }

    [Fact]
    public async Task Failed_scan_records_error_and_keeps_old_inventory()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var transport = new InventoryTransport();
        var jobs = new RemoteJobService([transport], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db));
        var scans = new ScanService(jobs, new InventoryStore(db));
        var id = (await computers.ImportAsync("pc-01"))[0].Id;
        await scans.ScanAsync(id);

        transport.Fail = "Zugriff verweigert";
        var outcome = await scans.ScanAsync(id);

        Assert.False(outcome.Success);
        var detail = await computers.GetDetailAsync(id);
        Assert.Equal("Zugriff verweigert", detail!.LastScanError);
        Assert.Single(detail.Software);

        transport.Fail = null;
        await scans.ScanAsync(id);
        Assert.Null((await computers.GetDetailAsync(id))!.LastScanError);
    }

    [Fact]
    public async Task Static_collection_members_can_be_added_and_removed_and_survive_computer_deletion()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var collections = new CollectionService(db);
        var added = await computers.ImportAsync("pc-01 pc-02 pc-03");
        var collection = await collections.SaveAsync(null, "Vertrieb", null, isDynamic: false, matchAll: true, []);

        Assert.Equal(3, await collections.AddMembersAsync(collection.Id, added.Select(c => c.Id)));
        Assert.Equal(0, await collections.AddMembersAsync(collection.Id, [added[0].Id]));
        Assert.Equal(1, await collections.RemoveMembersAsync(collection.Id, [added[0].Id]));
        await computers.DeleteAsync([added[1].Id]);

        Assert.Equal([added[2].Id], await collections.GetMemberIdsAsync(collection.Id));
    }

    private sealed class InventoryTransport : Core.Abstractions.IRemoteTransport
    {
        public TransportKind Kind => TransportKind.SmbService;
        public string ChromeVersion { get; set; } = "140.0";
        public string? Fail { get; set; }

        public Task<JobResult> RunAsync(Core.Abstractions.RemoteTarget target, JobManifest job, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            if (Fail is not null)
                throw new Core.Abstractions.RemoteException(Fail);
            return Task.FromResult(new JobResult
            {
                JobId = job.JobId, Success = true,
                Inventory = new InventoryData { Model = "Testmodell", Software = [new SoftwareItem { Name = "Google Chrome", Version = ChromeVersion }] },
            });
        }
    }
}
