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
