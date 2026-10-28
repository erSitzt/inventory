using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public sealed class DataTransferTests : IDisposable
{
    private const string Passphrase = "correct horse battery";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inventory-transfer-{Guid.NewGuid():N}");
    private string SourceDirectory => Path.Combine(_root, "source");
    private string TargetDirectory => Path.Combine(_root, "target");

    public DataTransferTests()
    {
        Directory.CreateDirectory(SourceDirectory);
        Directory.CreateDirectory(TargetDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Export_and_import_carry_the_state_and_reencrypt_secrets_with_the_target_keys()
    {
        using var source = new TestDb();
        var sourceKeys = new PrefixProtector("source:");
        await SeedAsync(source, sourceKeys);
        var packageFile = Path.Combine(SourceDirectory, "repository", "1", "setup", "app.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(packageFile)!);
        await File.WriteAllTextAsync(packageFile, "msi");

        var exporter = new DataTransferService(source, sourceKeys, SourceDirectory, TimeProvider.System, "LAPTOP-DEMO");
        var (id, manifest) = await exporter.ExportAsync(Passphrase, includePackages: true, "1.2.3");
        var zip = exporter.ExportFile(id)!;
        Assert.Equal("LAPTOP-DEMO", manifest.MachineName);
        Assert.Equal((1, 1, 1), (manifest.Computers, manifest.Packages, manifest.Credentials));
        Assert.True(manifest.IncludesPackages);
        Assert.Equal(3, manifest.PackageBytes);

        // Im Export steht kein Geheimnis im Klartext und keines mit dem Schlüssel der Quelle.
        var exportedDb = await ExtractDatabaseAsync(zip);
        await using (var db = Open(exportedDb))
        {
            var password = (await db.Credentials.SingleAsync()).ProtectedPassword;
            Assert.StartsWith("v1:", password);
            Assert.DoesNotContain("geheim", password);
        }

        using var target = new TestDb();
        var targetKeys = new PrefixProtector("target:");
        var importer = new DataTransferService(target, targetKeys, TargetDirectory, TimeProvider.System, "SRV-PROD");
        await File.WriteAllTextAsync(Path.Combine(TargetDirectory, "inventory.db"), "old");
        Directory.CreateDirectory(Path.Combine(TargetDirectory, "repository", "9"));

        var staged = await importer.StageImportAsync(zip, Passphrase, new AuditEntry { User = "admin", Action = "Data imported" });
        Assert.Equal("LAPTOP-DEMO", staged.MachineName);
        Assert.NotNull(importer.PendingImport);

        var previous = DataTransferService.ApplyPendingImport(TargetDirectory, new DateTime(2026, 10, 28, 12, 0, 0));

        Assert.NotNull(previous);
        Assert.Null(importer.PendingImport);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(previous, "inventory.db")));
        Assert.True(Directory.Exists(Path.Combine(previous, "repository", "9")));
        Assert.Equal("msi", await File.ReadAllTextAsync(Path.Combine(TargetDirectory, "repository", "1", "setup", "app.msi")));
        await using (var db = Open(Path.Combine(TargetDirectory, "inventory.db")))
        {
            Assert.Equal("pc-01", (await db.Computers.SingleAsync()).Name);
            Assert.Equal("Firefox", (await db.Packages.SingleAsync()).Name);
            Assert.Equal("geheim", targetKeys.Unprotect((await db.Credentials.SingleAsync()).ProtectedPassword));
            Assert.Equal("https://hooks.example/abc", targetKeys.Unprotect((await db.NotificationChannels.SingleAsync()).ProtectedUrl!));
            Assert.Equal("private", targetKeys.Unprotect((await db.NetworkRanges.SingleAsync()).ProtectedCommunities!));
            var settings = await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value);
            Assert.Equal("community", targetKeys.Unprotect(settings[SettingKeys.DeviceSnmpCommunities]!));
            // Die Adresse des Laptops wäre auf dem neuen Server falsch; was nicht am Rechner hängt, bleibt.
            Assert.False(settings.ContainsKey(SettingKeys.AgentServerUrl));
            Assert.Equal("25", settings[SettingKeys.MaxParallelTargets]);
            Assert.Contains(await db.AuditEntries.ToListAsync(), e => e.Action == "Data imported" && e.User == "admin");
        }
    }

    [Fact]
    public async Task Import_on_the_same_computer_keeps_its_machine_settings_and_the_repository_without_package_files()
    {
        using var db = new TestDb();
        var keys = new PrefixProtector("k:");
        await SeedAsync(db, keys);
        var service = new DataTransferService(db, keys, SourceDirectory, TimeProvider.System, "SRV");
        var (id, manifest) = await service.ExportAsync(Passphrase, includePackages: false, "1.2.3");
        Assert.False(manifest.IncludesPackages);
        var zip = Path.Combine(_root, "export.zip");
        File.Copy(service.ExportFile(id)!, zip);
        Directory.CreateDirectory(Path.Combine(SourceDirectory, "repository", "1"));

        await service.StageImportAsync(zip, Passphrase, new AuditEntry { User = "admin", Action = "Data imported" });
        SqliteConnection.ClearAllPools();
        DataTransferService.ApplyPendingImport(SourceDirectory, DateTime.Now);

        Assert.True(Directory.Exists(Path.Combine(SourceDirectory, "repository", "1")));
        await using var imported = Open(Path.Combine(SourceDirectory, "inventory.db"));
        Assert.Equal("https://inventory.example", (await imported.Settings.SingleAsync(s => s.Key == SettingKeys.AgentServerUrl)).Value);
    }

    [Fact]
    public async Task Wrong_passphrase_is_rejected_and_nothing_is_staged()
    {
        var zip = await ExportAsync();
        using var target = new TestDb();
        var importer = new DataTransferService(target, new PrefixProtector("t:"), TargetDirectory, TimeProvider.System);

        var error = await Assert.ThrowsAsync<TransferException>(() => importer.StageImportAsync(zip, "wrong passphrase", new AuditEntry { User = "a", Action = "x" }));

        Assert.Equal("The passphrase is wrong.", error.Format);
        Assert.Null(importer.PendingImport);
        Assert.False(Directory.Exists(Path.Combine(TargetDirectory, "import-pending")));
        Assert.Null(DataTransferService.ApplyPendingImport(TargetDirectory, DateTime.Now));
    }

    [Fact]
    public async Task Export_from_a_newer_version_is_rejected()
    {
        var zip = await ExportAsync();
        await RewriteManifestAsync(zip, manifest => manifest with { Migration = "29991231000000_Future", AppVersion = "9.0.0" });
        using var target = new TestDb();
        var importer = new DataTransferService(target, new PrefixProtector("t:"), TargetDirectory, TimeProvider.System);

        var error = await Assert.ThrowsAsync<TransferException>(() => importer.StageImportAsync(zip, Passphrase, new AuditEntry { User = "a", Action = "x" }));

        Assert.Contains("newer version", error.Format);
        Assert.Equal("9.0.0", error.Args[0]);
    }

    [Fact]
    public async Task Entries_outside_the_repository_folder_are_rejected()
    {
        var zip = await ExportAsync(includePackages: true);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            await using var entry = archive.CreateEntry("repository/../../evil.txt").Open();
            await entry.WriteAsync("x"u8.ToArray());
        }
        using var target = new TestDb();
        var importer = new DataTransferService(target, new PrefixProtector("t:"), TargetDirectory, TimeProvider.System);

        await Assert.ThrowsAsync<TransferException>(() => importer.StageImportAsync(zip, Passphrase, new AuditEntry { User = "a", Action = "x" }));

        Assert.False(File.Exists(Path.Combine(TargetDirectory, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(TargetDirectory, "import-pending", "evil.txt")));
        Assert.Null(importer.PendingImport);
    }

    [Fact]
    public async Task Not_an_export_is_rejected()
    {
        var zip = Path.Combine(_root, "other.zip");
        await File.WriteAllTextAsync(zip, "not a zip");

        Assert.Throws<TransferException>(() => DataTransferService.Inspect(zip));
    }

    [Fact]
    public async Task Short_passphrase_is_rejected()
    {
        using var db = new TestDb();
        var service = new DataTransferService(db, new PrefixProtector("k:"), SourceDirectory, TimeProvider.System);

        await Assert.ThrowsAsync<TransferException>(() => service.ExportAsync("short", false, "1.0.0"));
    }

    /// <summary>
    /// Ein neues verschlüsseltes Feld, das der Export nicht kennt, käme mit dem Schlüssel des alten Servers an und wäre dort
    /// unlesbar. Solche Felder heißen "Protected…" – dieser Test erinnert daran, sie in den Export aufzunehmen.
    /// </summary>
    [Fact]
    public void Every_protected_column_is_covered_by_the_export()
    {
        using var db = new TestDb();
        using var context = db.CreateDbContext();
        var protectedColumns = context.Model.GetEntityTypes()
            .SelectMany(type => type.GetProperties().Where(p => p.ClrType == typeof(string) && p.Name.StartsWith("Protected", StringComparison.Ordinal))
                .Select(p => $"{type.ClrType.Name}.{p.Name}"))
            .Order().ToList();

        Assert.Equal(["Credential.ProtectedPassword", "NetworkRange.ProtectedCommunities", "NotificationChannel.ProtectedUrl"], protectedColumns);
    }

    [Fact]
    public void Startup_removes_leftover_transfer_files()
    {
        var transfer = Path.Combine(TargetDirectory, "transfer");
        Directory.CreateDirectory(transfer);
        File.WriteAllText(Path.Combine(transfer, "upload-x.zip"), "");

        Assert.Null(DataTransferService.ApplyPendingImport(TargetDirectory, DateTime.Now));
        Assert.False(Directory.Exists(transfer));
    }

    private async Task<string> ExportAsync(bool includePackages = false)
    {
        using var source = new TestDb();
        var keys = new PrefixProtector("source:");
        await SeedAsync(source, keys);
        if (includePackages)
        {
            Directory.CreateDirectory(Path.Combine(SourceDirectory, "repository", "1"));
            await File.WriteAllTextAsync(Path.Combine(SourceDirectory, "repository", "1", "a.txt"), "a");
        }
        var exporter = new DataTransferService(source, keys, SourceDirectory, TimeProvider.System, "LAPTOP-DEMO");
        var (id, _) = await exporter.ExportAsync(Passphrase, includePackages, "1.2.3");
        var zip = Path.Combine(_root, $"export-{Guid.NewGuid():N}.zip");
        File.Copy(exporter.ExportFile(id)!, zip);
        return zip;
    }

    private static async Task SeedAsync(TestDb factory, ISecretProtector keys)
    {
        await using var db = factory.CreateDbContext();
        db.Computers.Add(new Computer { Name = "pc-01" });
        db.Packages.Add(new Package { Name = "Firefox" });
        db.Credentials.Add(new Credential { Name = "Admin", Username = @"CORP\deploy", ProtectedPassword = keys.Protect("geheim") });
        db.NotificationChannels.Add(new NotificationChannel { Name = "Teams", ProtectedUrl = keys.Protect("https://hooks.example/abc") });
        db.NetworkRanges.Add(new NetworkRange { Name = "Office", Addresses = "10.0.0.0/24", ProtectedCommunities = keys.Protect("private") });
        db.Settings.AddRange(
            new Setting { Key = SettingKeys.DeviceSnmpCommunities, Value = keys.Protect("community") },
            new Setting { Key = SettingKeys.AgentServerUrl, Value = "https://inventory.example" },
            new Setting { Key = SettingKeys.MaxParallelTargets, Value = "25" });
        await db.SaveChangesAsync();
    }

    private async Task<string> ExtractDatabaseAsync(string zip)
    {
        var path = Path.Combine(_root, $"extracted-{Guid.NewGuid():N}.db");
        using var archive = ZipFile.OpenRead(zip);
        archive.GetEntry("inventory.db")!.ExtractToFile(path);
        await Task.CompletedTask;
        return path;
    }

    private static async Task RewriteManifestAsync(string zip, Func<TransferManifest, TransferManifest> change)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        var entry = archive.GetEntry("manifest.json")!;
        TransferManifest manifest;
        await using (var stream = entry.Open())
            manifest = (await JsonSerializer.DeserializeAsync<TransferManifest>(stream, json))!;
        entry.Delete();
        await using var target = archive.CreateEntry("manifest.json").Open();
        await JsonSerializer.SerializeAsync(target, change(manifest), json);
    }

    private static InventoryDbContext Open(string path) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    /// <summary>Wie die Data-Protection-Schlüssel: was mit einem anderen Schlüssel geschützt wurde, lässt sich nicht lesen.</summary>
    private sealed class PrefixProtector(string prefix) : ISecretProtector
    {
        public string Protect(string plaintext) => prefix + plaintext;

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith(prefix, StringComparison.Ordinal) ? protectedValue[prefix.Length..] : throw new CryptographicException("Wrong key.");
    }
}
