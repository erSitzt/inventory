using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>
/// Sichert die Datenbank im laufenden Betrieb in den Ordner "backups" des Datenverzeichnisses, dazu die Schlüssel, ohne die
/// sich gespeicherte Passwörter aus einer Sicherung nicht mehr lesen lassen. Paketdateien gehören nicht dazu: sie sind groß
/// und ändern sich selten; wer sie braucht, sichert den Ordner des Datenverzeichnisses mit.
/// </summary>
/// <param name="keysDirectory">Ordner der Data-Protection-Schlüssel; null = keine Schlüssel sichern (Tests).</param>
public sealed class BackupService(IDbContextFactory<InventoryDbContext> dbFactory, string backupDirectory, TimeProvider time, string? keysDirectory = null)
{
    /// <summary>Ab diesem Anteil freier Seiten lohnt es, die Datei neu zu schreiben.</summary>
    private const double VacuumFreeShare = 0.25;
    /// <summary>Unterhalb dieser Größe (in Seiten, also rund 20 MB) ist verschenkter Platz belanglos.</summary>
    private const long VacuumMinPages = 5000;

    private const int KeepBackups = 14;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public string Directory => backupDirectory;

    public async Task<string> BackupAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, $"inventory-{time.GetLocalNow():yyyyMMdd-HHmmss}.db");
        // Zuerst die Schlüssel: scheitert das, gibt es noch keine Datenbankkopie, und der nächste Takt versucht die ganze Sicherung erneut.
        if (keysDirectory is not null && System.IO.Directory.Exists(keysDirectory) && System.IO.Directory.EnumerateFiles(keysDirectory).Any())
        {
            // Unter Windows sind die Schlüssel an den Rechner gebunden (DPAPI): sie helfen beim Zurückspielen auf demselben Server.
            var keys = Path.ChangeExtension(path, null) + KeysSuffix;
            var partial = keys + ".tmp";
            File.Delete(partial);
            System.IO.Compression.ZipFile.CreateFromDirectory(keysDirectory, partial);
            File.Move(partial, keys, overwrite: true);
        }
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // VACUUM INTO schreibt eine konsistente Kopie, auch während andere Verbindungen lesen und schreiben.
            await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [path], ct);
        }

        foreach (var pattern in new[] { "inventory-*.db", "inventory-*" + KeysSuffix })
        {
            foreach (var old in System.IO.Directory.GetFiles(backupDirectory, pattern).OrderDescending().Skip(KeepBackups))
                File.Delete(old);
        }
        return path;
    }

    private const string KeysSuffix = "-keys.zip";

    /// <summary>
    /// Pflege nach dem täglichen Aufräumen: aktualisiert die Statistiken des Abfrageplaners und gibt den Platz gelöschter
    /// Zeilen zurück, wenn ein großer Teil der Datei leer ist.
    /// </summary>
    /// <param name="busy">true = gerade laufen Deployments oder Scans; das Neuschreiben der Datei wartet dann bis zum nächsten Tag.</param>
    /// <returns>true, wenn die Datei neu geschrieben wurde.</returns>
    public async Task<bool> OptimizeAsync(bool busy = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA optimize", ct);
        var free = await db.Database.SqlQueryRaw<long>("SELECT freelist_count AS Value FROM pragma_freelist_count").SingleAsync(ct);
        var pages = await db.Database.SqlQueryRaw<long>("SELECT page_count AS Value FROM pragma_page_count").SingleAsync(ct);
        if (busy || pages < VacuumMinPages || free < pages * VacuumFreeShare)
            return false;
        await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
        return true;
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
