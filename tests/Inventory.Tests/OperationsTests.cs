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
