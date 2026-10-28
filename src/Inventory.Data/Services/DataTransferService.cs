using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Beschreibung einer Exportdatei; liegt als manifest.json neben der Datenbank im ZIP.</summary>
/// <param name="Migration">Jüngste Migration der exportierten Datenbank: ein Import braucht eine Version, die sie kennt.</param>
/// <param name="Salt">Salz der Schlüsselableitung aus der Passphrase (Base64).</param>
/// <param name="Check">Ein bekannter Text, mit der Passphrase verschlüsselt: prüft sie, bevor etwas entpackt wird.</param>
public sealed record TransferManifest(int Format, string AppVersion, string Migration, DateTime CreatedUtc, string MachineName,
    bool IncludesPackages, string Salt, int Iterations, string Check, int Computers, int Packages, int Credentials, long PackageBytes);

/// <summary>Ein Fehler beim Export oder Import, den die Oberfläche übersetzt anzeigt (englischer Quelltext mit Platzhaltern).</summary>
public sealed class TransferException(string format, params object[] args) : Exception(string.Format(format, args))
{
    public string Format { get; } = format;
    public object[] Args { get; } = args;
}

/// <summary>
/// Überträgt den ganzen Stand einer Installation auf eine andere (oder sichert ihn): Datenbank und auf Wunsch die Paketdateien
/// in einem ZIP. Gespeicherte Geheimnisse (Passwörter, Webhook-Adressen, SNMP-Communities) sind auf dem Server mit Schlüsseln
/// verschlüsselt, die unter Windows an den Rechner gebunden sind; im Export sind sie stattdessen mit einer Passphrase verschlüsselt.
/// Ein Import ersetzt den Stand vollständig. Er wird vorbereitet (geprüft, migriert, Geheimnisse mit den Schlüsseln dieses Servers
/// neu verschlüsselt) und beim nächsten Start eingesetzt, bevor jemand die Datenbank öffnet.
/// </summary>
/// <param name="machineName">Name dieses Rechners; null = der echte (Tests geben einen anderen vor).</param>
public sealed class DataTransferService(IDbContextFactory<InventoryDbContext> dbFactory, ISecretProtector protector, string dataDirectory, TimeProvider time, string? machineName = null)
{
    private readonly string _machineName = machineName ?? Environment.MachineName;

    public const int CurrentFormat = 1;
    public const int MinPassphraseLength = 8;
    /// <summary>Empfehlung für PBKDF2 mit SHA-256 (OWASP 2023).</summary>
    private const int Iterations = 600_000;
    private const string CheckText = "Inventory export";
    private const string ManifestEntry = "manifest.json";
    private const string DatabaseEntry = "inventory.db";
    private const string RepositoryEntry = "repository/";

    /// <summary>Gehören zum Rechner, nicht zum Bestand: auf einem anderen Server wären sie falsch.</summary>
    internal static readonly string[] MachineSettings =
    [
        SettingKeys.AgentServerUrl, SettingKeys.NetworkMapServerAddresses,
        SettingKeys.HeartbeatLastRunUtc, SettingKeys.HeartbeatLastResult, SettingKeys.AdSyncLastRunUtc, SettingKeys.AdSyncLastResult,
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string TransferDirectory => Path.Combine(dataDirectory, "transfer");
    private string PendingDirectory => Path.Combine(dataDirectory, "import-pending");
    private string ReadyDirectory => Path.Combine(PendingDirectory, "ready");
    private string RepositoryDirectory => Path.Combine(dataDirectory, "repository");

    /// <summary>Wartet ein vorbereiteter Import auf den Neustart, seine Beschreibung; sonst null.</summary>
    public TransferManifest? PendingImport
    {
        get
        {
            var manifest = Path.Combine(ReadyDirectory, ManifestEntry);
            return File.Exists(manifest) ? JsonSerializer.Deserialize<TransferManifest>(File.ReadAllText(manifest), Json) : null;
        }
    }

    /// <summary>Verwirft einen vorbereiteten Import.</summary>
    public void CancelPendingImport()
    {
        if (Directory.Exists(PendingDirectory))
            Directory.Delete(PendingDirectory, recursive: true);
    }

    /// <summary>Pfad einer fertigen Exportdatei zu ihrer Kennung; null, wenn es sie nicht (mehr) gibt.</summary>
    public string? ExportFile(Guid id)
    {
        var path = Path.Combine(TransferDirectory, $"export-{id:N}.zip");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Ablage für eine hochgeladene Exportdatei; liegengebliebene räumt der nächste Start weg.</summary>
    public string NewUploadPath()
    {
        Directory.CreateDirectory(TransferDirectory);
        return Path.Combine(TransferDirectory, $"upload-{Guid.NewGuid():N}.zip");
    }

    public void DeleteUpload(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(TransferDirectory))
            TryDelete(path);
    }

    /// <summary>Schreibt den Export in den Ordner "transfer" des Datenverzeichnisses und liefert seine Kennung.</summary>
    public async Task<(Guid Id, TransferManifest Manifest)> ExportAsync(string passphrase, bool includePackages, string appVersion, CancellationToken ct = default)
    {
        RequirePassphrase(passphrase);
        Directory.CreateDirectory(TransferDirectory);
        var id = Guid.NewGuid();
        var copy = Path.Combine(TransferDirectory, $"export-{id:N}.db");
        var zip = Path.Combine(TransferDirectory, $"export-{id:N}.zip");
        var salt = RandomNumberGenerator.GetBytes(16);
        var cipher = new PassphraseCipher(passphrase, salt, Iterations);
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [copy], ct);

            TransferManifest manifest;
            await using (var db = Open(copy))
            {
                await ReplaceSecretsAsync(db, protector.Unprotect, cipher.Seal, ct);
                var packageBytes = includePackages && Directory.Exists(RepositoryDirectory)
                    ? Directory.EnumerateFiles(RepositoryDirectory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                    : 0;
                manifest = new TransferManifest(CurrentFormat, appVersion, (await db.Database.GetAppliedMigrationsAsync(ct)).Last(), time.GetUtcNow().UtcDateTime,
                    _machineName, includePackages, Convert.ToBase64String(salt), Iterations, cipher.Seal(CheckText),
                    await db.Computers.CountAsync(ct), await db.Packages.CountAsync(ct), await db.Credentials.CountAsync(ct), packageBytes);
            }
            SqliteConnection.ClearAllPools();

            var partial = zip + ".tmp";
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await using (var entry = archive.CreateEntry(ManifestEntry).Open())
                    await JsonSerializer.SerializeAsync(entry, manifest, Json, ct);
                await AddFileAsync(archive, copy, DatabaseEntry, CompressionLevel.Optimal, ct);
                if (includePackages && Directory.Exists(RepositoryDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(RepositoryDirectory, "*", SearchOption.AllDirectories))
                    {
                        // Halbe Uploads gehören nicht dazu.
                        if (file.EndsWith(".upload", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var name = RepositoryEntry + Path.GetRelativePath(RepositoryDirectory, file).Replace('\\', '/');
                        // Installationsdateien sind meist schon komprimiert.
                        await AddFileAsync(archive, file, name, CompressionLevel.Fastest, ct);
                    }
                }
            }
            File.Move(partial, zip, overwrite: true);
            return (id, manifest);
        }
        catch
        {
            TryDelete(zip + ".tmp");
            TryDelete(zip);
            throw;
        }
        finally
        {
            TryDelete(copy);
        }
    }

    /// <summary>
    /// Prüft einen Export und bereitet ihn als Import vor: entpacken, auf den Stand dieser Version migrieren und die Geheimnisse
    /// mit den Schlüsseln dieses Servers verschlüsseln. Eingesetzt wird er erst mit <see cref="ApplyPendingImport"/> beim Start.
    /// </summary>
    /// <param name="audit">Wird in die importierte Datenbank geschrieben; das Protokoll des jetzigen Stands bleibt in der Sicherung.</param>
    public async Task<TransferManifest> StageImportAsync(string zipPath, string passphrase, AuditEntry audit, CancellationToken ct = default)
    {
        CancelPendingImport();
        var staging = Path.Combine(PendingDirectory, "staging");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = OpenArchive(zipPath);
            var manifest = ReadManifest(archive);
            PassphraseCipher cipher;
            try
            {
                cipher = new PassphraseCipher(passphrase, Convert.FromBase64String(manifest.Salt), manifest.Iterations);
                if (cipher.Open(manifest.Check) != CheckText)
                    throw new CryptographicException();
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new TransferException("The passphrase is wrong.");
            }
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (!db.Database.GetMigrations().Contains(manifest.Migration))
                    throw new TransferException("The export comes from a newer version of Inventory ({0}). Update this installation first.", manifest.AppVersion);
            }

            var database = Path.Combine(staging, DatabaseEntry);
            var repository = Path.Combine(staging, "repository");
            var root = Path.GetFullPath(repository) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName == DatabaseEntry)
                    entry.ExtractToFile(database);
                else if (manifest.IncludesPackages && entry.FullName.StartsWith(RepositoryEntry, StringComparison.Ordinal) && !entry.FullName.EndsWith('/'))
                {
                    var target = Path.GetFullPath(Path.Combine(repository, entry.FullName[RepositoryEntry.Length..]));
                    // Keine Einträge mit ".." oder absoluten Pfaden, die außerhalb des Ordners landen würden.
                    if (!target.StartsWith(root, StringComparison.Ordinal))
                        throw new TransferException("The export file is damaged or not an Inventory export.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            }
            if (!File.Exists(database))
                throw new TransferException("The export file is damaged or not an Inventory export.");

            await using (var db = Open(database))
            {
                await db.Database.MigrateAsync(ct);
                await ReplaceSecretsAsync(db, cipher.Open, protector.Protect, ct);
                if (!string.Equals(manifest.MachineName, _machineName, StringComparison.OrdinalIgnoreCase))
                    await db.Settings.Where(s => MachineSettings.Contains(s.Key)).ExecuteDeleteAsync(ct);
                db.AuditEntries.Add(audit);
                await db.SaveChangesAsync(ct);
            }
            SqliteConnection.ClearAllPools();

            await File.WriteAllTextAsync(Path.Combine(staging, ManifestEntry), JsonSerializer.Serialize(manifest, Json), ct);
            // Erst das Umbenennen macht den Import gültig: ein abgebrochenes Vorbereiten setzt beim Start nichts ein.
            Directory.Move(staging, ReadyDirectory);
            return manifest;
        }
        catch (InvalidDataException)
        {
            SqliteConnection.ClearAllPools();
            CancelPendingImport();
            throw new TransferException("The export file is damaged or not an Inventory export.");
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            CancelPendingImport();
            throw;
        }
    }

    /// <summary>
    /// Setzt beim Start einen vorbereiteten Import ein, bevor die Datenbank geöffnet wird. Der bisherige Stand (Datenbank und,
    /// wenn der Import Paketdateien mitbringt, das Repository) wandert nach backups\before-import-…; gelöscht wird nichts.
    /// Räumt außerdem liegengebliebene Exportdateien weg.
    /// </summary>
    /// <returns>Der Ordner mit dem bisherigen Stand, wenn ein Import eingesetzt wurde; sonst null.</returns>
    public static string? ApplyPendingImport(string dataDirectory, DateTime localNow)
    {
        var transfer = Path.Combine(dataDirectory, "transfer");
        if (Directory.Exists(transfer))
            Directory.Delete(transfer, recursive: true);

        var pending = Path.Combine(dataDirectory, "import-pending");
        var ready = Path.Combine(pending, "ready");
        if (!File.Exists(Path.Combine(ready, DatabaseEntry)))
        {
            if (Directory.Exists(pending))
                Directory.Delete(pending, recursive: true);
            return null;
        }

        var previous = Path.Combine(dataDirectory, "backups", $"before-import-{localNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(previous);
        var database = Path.Combine(dataDirectory, DatabaseEntry);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(database + suffix))
                File.Move(database + suffix, Path.Combine(previous, DatabaseEntry + suffix));
        }
        var repository = Path.Combine(dataDirectory, "repository");
        var importedRepository = Path.Combine(ready, "repository");
        if (Directory.Exists(importedRepository))
        {
            if (Directory.Exists(repository))
                Directory.Move(repository, Path.Combine(previous, "repository"));
            Directory.Move(importedRepository, repository);
        }
        File.Move(Path.Combine(ready, DatabaseEntry), database);
        Directory.Delete(pending, recursive: true);
        return previous;
    }

    /// <summary>Liest nur die Beschreibung eines Exports, z. B. zur Anzeige vor dem Import.</summary>
    public static TransferManifest Inspect(string zipPath)
    {
        try
        {
            using var archive = OpenArchive(zipPath);
            return ReadManifest(archive);
        }
        catch (InvalidDataException)
        {
            throw new TransferException("The export file is damaged or not an Inventory export.");
        }
    }

    public static void RequirePassphrase(string passphrase)
    {
        if (passphrase.Length < MinPassphraseLength)
            throw new TransferException("The passphrase needs at least {0} characters.", MinPassphraseLength);
    }

    /// <summary>
    /// Alle verschlüsselt gespeicherten Werte. Kommt ein neuer hinzu, gehört er hierher – ein Test prüft das anhand der
    /// Spalten, deren Name mit "Protected" beginnt.
    /// </summary>
    private static async Task ReplaceSecretsAsync(InventoryDbContext db, Func<string, string> open, Func<string, string> seal, CancellationToken ct)
    {
        foreach (var credential in await db.Credentials.ToListAsync(ct))
            credential.ProtectedPassword = Reseal(open, seal, credential.ProtectedPassword, "credential", credential.Name)!;
        foreach (var channel in await db.NotificationChannels.ToListAsync(ct))
            channel.ProtectedUrl = Reseal(open, seal, channel.ProtectedUrl, "notification channel", channel.Name);
        foreach (var range in await db.NetworkRanges.ToListAsync(ct))
            range.ProtectedCommunities = Reseal(open, seal, range.ProtectedCommunities, "network range", range.Name);
        if (await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DeviceSnmpCommunities, ct) is { } communities)
            communities.Value = Reseal(open, seal, communities.Value, "setting", SettingKeys.DeviceSnmpCommunities);
        await db.SaveChangesAsync(ct);
    }

    private static string? Reseal(Func<string, string> open, Func<string, string> seal, string? value, string kind, string name)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        try
        {
            return seal(open(value));
        }
        catch (CryptographicException)
        {
            throw new TransferException("The stored secret of {0} \"{1}\" cannot be decrypted. Enter it again and repeat the export.", kind, name);
        }
    }

    private static ZipArchive OpenArchive(string zipPath) => new(File.OpenRead(zipPath), ZipArchiveMode.Read);

    private static TransferManifest ReadManifest(ZipArchive archive)
    {
        TransferManifest? manifest = null;
        if (archive.GetEntry(ManifestEntry) is { } entry)
        {
            try
            {
                using var stream = entry.Open();
                manifest = JsonSerializer.Deserialize<TransferManifest>(stream, Json);
            }
            catch (JsonException)
            {
            }
        }
        if (manifest is null || string.IsNullOrEmpty(manifest.Migration) || string.IsNullOrEmpty(manifest.Salt) || manifest.Iterations <= 0)
            throw new TransferException("The export file is damaged or not an Inventory export.");
        if (manifest.Format > CurrentFormat)
            throw new TransferException("The export comes from a newer version of Inventory ({0}). Update this installation first.", manifest.AppVersion);
        return manifest;
    }

    private static InventoryDbContext Open(string path) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task AddFileAsync(ZipArchive archive, string path, string name, CompressionLevel level, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, level);
        entry.LastWriteTime = File.GetLastWriteTime(path);
        await using var target = entry.Open();
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await source.CopyToAsync(target, ct);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>AES-GCM mit einem Schlüssel, der per PBKDF2 (SHA-256) aus der Passphrase abgeleitet ist.</summary>
internal sealed class PassphraseCipher(string passphrase, byte[] salt, int iterations)
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);

    public string Seal(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var result = new byte[NonceSize + TagSize + plain.Length];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(result);
    }

    /// <exception cref="CryptographicException">Falsche Passphrase oder veränderte Daten.</exception>
    public string Open(string sealedValue)
    {
        if (!sealedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unknown format.");
        byte[] data;
        try
        {
            data = Convert.FromBase64String(sealedValue[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException(ex.Message, ex);
        }
        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Too short.");
        var plain = new byte[data.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
