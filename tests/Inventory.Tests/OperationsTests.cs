using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class OperationsTests
{
    [Fact]
    public async Task Audit_entries_are_listed_newest_first_and_long_details_are_cut()
    {
        using var db = new TestDb();
        var audit = new AuditService(db);

        await audit.LogAsync(@"CORP\anna", "Zugangsdaten angelegt", "Deploy");
        await audit.LogAsync(@"CORP\ben", "Remote-Befehl", new string('x', 5000));

        var entries = await audit.ListAsync();
        Assert.Equal([@"CORP\ben", @"CORP\anna"], entries.Select(e => e.User));
        Assert.True(entries[0].Details!.Length < 2100);
    }

    [Fact]
    public async Task Backup_creates_a_readable_copy_keeps_only_recent_ones_and_runs_once_per_day()
    {
        using var db = new TestDb();
        var dir = Directory.CreateTempSubdirectory("inventory-backup-").FullName;
        try
        {
            await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01 pc-02");
            for (var i = 0; i < 16; i++)
            {
                var old = Path.Combine(dir, $"inventory-2020010{i % 10}-0000{i:D2}.db");
                File.WriteAllText(old, "alt");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
            }
            var backups = new BackupService(db, dir, TimeProvider.System);

            Assert.True(await backups.BackupIfDueAsync());
            Assert.False(await backups.BackupIfDueAsync());

            var files = Directory.GetFiles(dir);
            Assert.Equal(14, files.Length);
            var newest = files.OrderDescending().First();
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={newest};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Computers";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_includes_the_keys_and_prunes_them_like_the_database()
    {
        using var db = new TestDb();
        var dir = Directory.CreateTempSubdirectory("inventory-backup-").FullName;
        var keys = Directory.CreateTempSubdirectory("inventory-keys-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(keys, "key-1.xml"), "<key />");
            for (var i = 0; i < 16; i++)
                File.WriteAllText(Path.Combine(dir, $"inventory-20200101-0000{i:D2}-keys.zip"), "alt");

            var path = await new BackupService(db, dir, TimeProvider.System, keys).BackupAsync();

            var archive = Path.ChangeExtension(path, null) + "-keys.zip";
            using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
                Assert.Equal(["key-1.xml"], zip.Entries.Select(e => e.FullName));
            Assert.Equal(14, Directory.GetFiles(dir, "*-keys.zip").Length);
            // Ohne Schlüssel (noch keiner angelegt) entsteht kein leeres Archiv.
            var empty = Directory.CreateTempSubdirectory("inventory-keys-").FullName;
            var other = Directory.CreateTempSubdirectory("inventory-backup-").FullName;
            await new BackupService(db, other, TimeProvider.System, empty).BackupAsync();
            Assert.Empty(Directory.GetFiles(other, "*-keys.zip"));
            Directory.Delete(empty);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(other, recursive: true);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
            Directory.Delete(keys, recursive: true);
        }
    }

    [Fact]
    public async Task Optimizing_rewrites_the_file_only_when_much_of_it_is_free_and_nothing_runs()
    {
        using var db = new TestDb();
        var backups = new BackupService(db, Path.GetTempPath(), TimeProvider.System);
        // Eine kleine Datenbank bleibt, wie sie ist.
        Assert.False(await backups.OptimizeAsync());

        var audit = new AuditService(db);
        await using (var context = db.CreateDbContext())
        {
            context.AuditEntries.AddRange(Enumerable.Range(0, 6000).Select(i => new Inventory.Core.Model.AuditEntry { User = "u", Action = "a", Details = new string('x', 4000) }));
            await context.SaveChangesAsync();
            await context.AuditEntries.Where(e => e.Id > 0).ExecuteDeleteAsync();
        }

        Assert.False(await backups.OptimizeAsync(busy: true));
        Assert.True(await backups.OptimizeAsync());
        Assert.False(await backups.OptimizeAsync());
        Assert.Empty(await audit.ListAsync());
    }

    [Fact]
    public async Task Old_finished_deployments_are_removed_but_not_waiting_ones_or_those_of_required_schedules()
    {
        using var db = new TestDb();
        var repo = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
        try
        {
            var computers = new ComputerService(db, new SimulatedHostProbe());
            var packages = new PackageService(db, new Inventory.Data.PackageRepository(repo));
            var deployments = new DeploymentService(db, packages,
                new RemoteJobService([new SimulatedTransport(Inventory.Core.Model.TransportKind.SmbService)], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db)),
                new InventoryStore(db), new SimulatedHostProbe());
            var old = DateTime.UtcNow.AddDays(-100);
            await using (var context = db.CreateDbContext())
            {
                var required = new Inventory.Core.Model.Schedule { Name = "Pflicht", Action = Inventory.Core.Model.ScheduleAction.Deploy, Trigger = Inventory.Core.Model.ScheduleTrigger.OnMembership };
                var weekly = new Inventory.Core.Model.Schedule { Name = "Woche", Action = Inventory.Core.Model.ScheduleAction.Deploy, Trigger = Inventory.Core.Model.ScheduleTrigger.Weekly };
                context.Schedules.AddRange(required, weekly);
                await context.SaveChangesAsync();
                Inventory.Core.Model.Deployment Add(string name, DateTime created, Inventory.Core.Model.TargetStatus status, int? scheduleId = null) => new()
                {
                    PackageName = name, CreatedUtc = created, FinishedUtc = created.AddMinutes(5), ScheduleId = scheduleId,
                    Targets = [new Inventory.Core.Model.DeploymentTarget { ComputerName = "pc-01", Status = status, FinishedUtc = status.IsWaiting() ? null : created.AddMinutes(5) }],
                };
                context.Deployments.AddRange(
                    Add("alt", old, Inventory.Core.Model.TargetStatus.Succeeded),
                    Add("alt-zeitplan", old, Inventory.Core.Model.TargetStatus.Failed, weekly.Id),
                    Add("alt-wartet", old, Inventory.Core.Model.TargetStatus.WaitingOffline),
                    Add("alt-pflicht", old, Inventory.Core.Model.TargetStatus.Succeeded, required.Id),
                    Add("neu", DateTime.UtcNow.AddDays(-10), Inventory.Core.Model.TargetStatus.Succeeded));
                await context.SaveChangesAsync();
            }

            Assert.Equal(0, await deployments.CleanupAsync(0));
            Assert.Equal(2, await deployments.CleanupAsync(90));

            await using var after = db.CreateDbContext();
            Assert.Equal(["alt-pflicht", "alt-wartet", "neu"], after.Deployments.Select(d => d.PackageName).ToList().Order());
            // Die Ziele gehen mit.
            Assert.Equal(3, after.DeploymentTargets.Count());
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task The_deployment_retention_is_unlimited_or_at_least_thirty_days()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);

        Assert.Equal(SettingsService.DefaultDeploymentRetentionDays, await settings.GetDeploymentRetentionDaysAsync());
        await settings.SetDeploymentRetentionDaysAsync(5);
        Assert.Equal(30, await settings.GetDeploymentRetentionDaysAsync());
        await settings.SetDeploymentRetentionDaysAsync(0);
        Assert.Equal(0, await settings.GetDeploymentRetentionDaysAsync());
        await settings.SetDeploymentRetentionDaysAsync(400);
        Assert.Equal(400, await settings.GetDeploymentRetentionDaysAsync());
    }
}

public class ApiTokenTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Token_is_only_stored_hashed_validates_until_expiry_and_can_be_revoked()
    {
        using var db = new TestDb();
        var clock = new Clock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var tokens = new ApiTokenService(db, clock);

        var plain = await tokens.CreateAsync(" Monitoring ", @"CORP\anna", clock.Now.UtcDateTime.AddDays(30));
        var forever = await tokens.CreateAsync("Upload", @"CORP\anna", null);

        var stored = (await tokens.ListAsync()).Single(t => t.Name == "Monitoring");
        Assert.StartsWith("inv_", plain);
        Assert.DoesNotContain(plain, stored.TokenHash);
        Assert.Equal(plain[..10], stored.Prefix);
        Assert.Null(stored.LastUsedUtc);

        Assert.Equal("Monitoring", (await tokens.ValidateAsync(plain))!.Name);
        Assert.NotNull((await tokens.ListAsync()).Single(t => t.Name == "Monitoring").LastUsedUtc);
        Assert.Null(await tokens.ValidateAsync(plain + "x"));
        Assert.Null(await tokens.ValidateAsync("inv_"));
        Assert.Null(await tokens.ValidateAsync(null));

        clock.Now = clock.Now.AddDays(31);
        Assert.Null(await tokens.ValidateAsync(plain));
        Assert.NotNull(await tokens.ValidateAsync(forever));

        await tokens.DeleteAsync((await tokens.ListAsync()).Single(t => t.Name == "Upload").Id);
        Assert.Null(await tokens.ValidateAsync(forever));
    }
}
