using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Sichert die Datenbank im laufenden Betrieb in den Ordner "backups" des Datenverzeichnisses.</summary>
public sealed class BackupService(IDbContextFactory<InventoryDbContext> dbFactory, string backupDirectory, TimeProvider time)
{
    private const int KeepBackups = 14;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public string Directory => backupDirectory;

    public async Task<string> BackupAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, $"inventory-{time.GetLocalNow():yyyyMMdd-HHmmss}.db");
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // VACUUM INTO schreibt eine konsistente Kopie, auch während andere Verbindungen lesen und schreiben.
            await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [path], ct);
        }

        foreach (var old in System.IO.Directory.GetFiles(backupDirectory, "inventory-*.db").OrderDescending().Skip(KeepBackups))
            File.Delete(old);
        return path;
    }

    /// <summary>Sichert, wenn die letzte Sicherung älter als ein Tag ist.</summary>
    public async Task<bool> BackupIfDueAsync(CancellationToken ct = default)
    {
        var latest = System.IO.Directory.Exists(backupDirectory)
            ? System.IO.Directory.GetFiles(backupDirectory, "inventory-*.db").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()
            : DateTime.MinValue;
        if (time.GetUtcNow().UtcDateTime - latest < Interval)
            return false;
        await BackupAsync(ct);
        return true;
    }
}
