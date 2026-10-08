using System.Security.Cryptography;
using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;

namespace Inventory.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Directory.CreateTempSubdirectory("inventory-catalog-").FullName;
    private readonly FakeCatalog _source = new();
    private readonly PackageService _packages;
    private readonly CollectionService _collections;
    private readonly CatalogService _catalog;

    public CatalogTests()
    {
        _packages = new PackageService(_db, new PackageRepository(_repoDir));
        _collections = new CollectionService(_db);
        _catalog = new CatalogService(_db, _source, _packages, _collections, new AuditService(_db), TimeProvider.System, "test");
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    [Fact]
    public async Task Import_downloads_the_setup_and_creates_a_package_with_install_step_and_outdated_collection()
    {
        _source.Publish("7zip.7zip", "7-Zip", "24.08", "");
        await _catalog.RefreshAsync();

        var package = await _catalog.ImportAsync("7zip.7zip", autoUpdate: true, createCollection: true, "anna");

        Assert.Equal(("7-Zip", "24.08", "7zip.7zip", true), (package.Name, package.Version, package.CatalogId, package.CatalogAutoUpdate));
        var step = Assert.Single(package.Steps).Definition;
        Assert.Equal((StepType.Install, "7zip.7zip-24.08.msi", ""), (step.Type, step.File, step.Arguments));
        Assert.Equal([0, 1641, 3010], step.SuccessCodes);
        Assert.Equal("7zip.7zip-24.08.msi", Assert.Single(_packages.Repository.List(package.Id)).RelativePath);

        var collection = (await _collections.GetAsync(package.CatalogCollectionId!.Value))!;
        Assert.Equal("7-Zip veraltet", collection.Name);
        var outdated = new Computer { Name = "alt", Software = [new ComputerSoftware { Name = "7-Zip 23.01 (x64)", Version = "23.01" }] };
        var current = new Computer { Name = "neu", Software = [new ComputerSoftware { Name = "7-Zip 24.08 (x64)", Version = "24.08" }] };
        Assert.True(CollectionEvaluator.Matches(collection, outdated));
        Assert.False(CollectionEvaluator.Matches(collection, current));

        var item = (await _catalog.ListAsync()).Single();
        Assert.False(item.UpdateAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.ImportAsync("7zip.7zip", false, false, "anna"));
    }

    [Fact]
    public async Task Update_replaces_the_setup_keeps_own_steps_and_moves_the_collection_threshold()
    {
        _source.Publish("7zip.7zip", "7-Zip", "24.08", "");
        await _catalog.RefreshAsync();
        var package = await _catalog.ImportAsync("7zip.7zip", autoUpdate: false, createCollection: true, "anna");
        await _packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Verknüpfung entfernen", Type = StepType.Command, Script = "del x" }, null);
        await _packages.Repository.SaveAsync(package.Id, "eigene.cfg", new MemoryStream([1]));

        _source.Publish("7zip.7zip", "7-Zip", "25.01", "/norestart");
        await _catalog.RefreshAsync(force: true);
        Assert.True((await _catalog.ListAsync()).Single().UpdateAvailable);
        // Ohne Automatik passiert von selbst nichts.
        Assert.Equal(0, await _catalog.UpdateDueAsync());

        Assert.True(await _catalog.UpdateAsync(package.Id, "anna"));

        var updated = (await _packages.GetAsync(package.Id))!;
        Assert.Equal("25.01", updated.Version);
        Assert.Equal(["7-Zip installieren", "Verknüpfung entfernen"], updated.Steps.Select(s => s.Definition.Name));
        Assert.Equal(("7zip.7zip-25.01.msi", "/norestart"), (updated.Steps[0].Definition.File, updated.Steps[0].Definition.Arguments));
        Assert.Equal(["7zip.7zip-25.01.msi", "eigene.cfg"], _packages.Repository.List(package.Id).Select(f => f.RelativePath));
        Assert.Equal("25.01", (await _collections.GetAsync(updated.CatalogCollectionId!.Value))!.Rules.Single().Value2);
        Assert.False(await _catalog.UpdateAsync(package.Id, "anna"));

        // Mit Automatik zieht der Zeitplaner neue Versionen selbst nach.
        await _catalog.SetAutoUpdateAsync(package.Id, true);
        _source.Publish("7zip.7zip", "7-Zip", "25.02", "");
        await _catalog.RefreshAsync(force: true);
        Assert.Equal(1, await _catalog.UpdateDueAsync());
        Assert.Equal("25.02", (await _packages.GetAsync(package.Id))!.Version);
    }

    [Fact]
    public async Task Wrong_checksum_aborts_import_and_update_without_leaving_anything_behind()
    {
        _source.Publish("7zip.7zip", "7-Zip", "24.08", "");
        await _catalog.RefreshAsync();
        _source.Corrupt = true;

        var error = await Assert.ThrowsAsync<RemoteException>(() => _catalog.ImportAsync("7zip.7zip", false, true, "anna"));
        Assert.Contains("Prüfsumme", error.Message);
        Assert.Empty(await _packages.ListAsync());
        Assert.Empty(await _collections.ListAsync());

        _source.Corrupt = false;
        var package = await _catalog.ImportAsync("7zip.7zip", false, false, "anna");
        _source.Publish("7zip.7zip", "7-Zip", "25.01", "");
        await _catalog.RefreshAsync(force: true);
        _source.Corrupt = true;
        await Assert.ThrowsAsync<RemoteException>(() => _catalog.UpdateAsync(package.Id, "anna"));
        Assert.Equal("24.08", (await _packages.GetAsync(package.Id))!.Version);
        Assert.Equal("7zip.7zip-24.08.msi", Assert.Single(_packages.Repository.List(package.Id)).RelativePath);
    }

    [Fact]
    public async Task Unreachable_catalog_is_reported_and_an_existing_package_name_is_not_overwritten()
    {
        _source.Fail = "Server nicht erreichbar";
        await _catalog.RefreshAsync();
        Assert.Equal("Server nicht erreichbar", _catalog.LastError);
        Assert.Empty(await _catalog.ListAsync());

        _source.Fail = null;
        _source.Publish("7zip.7zip", "7-Zip", "24.08", "");
        await _catalog.RefreshAsync(force: true);
        await _packages.SaveAsync(null, "7-Zip", "eigene", null);

        var imported = await _catalog.ImportAsync("7zip.7zip", false, false, "anna");
        Assert.Equal("7-Zip (Bibliothek)", imported.Name);
    }

    private sealed class FakeCatalog : ICatalogSource
    {
        private readonly Dictionary<string, CatalogEntry> _entries = [];
        public bool Corrupt { get; set; }
        public string? Fail { get; set; }

        private static byte[] Content(string version) => System.Text.Encoding.UTF8.GetBytes("setup " + version);

        public void Publish(string id, string name, string version, string arguments) => _entries[id] = new CatalogEntry
        {
            Id = id, Name = name, Version = version, Publisher = "Hersteller", Description = "Beschreibung", InstallerType = "wix",
            Url = version, Sha256 = Convert.ToHexString(SHA256.HashData(Content(version))).ToLowerInvariant(),
            FileName = $"{id}-{version}.msi", Arguments = arguments, SuccessCodes = [0, 1641, 3010], Match = name,
        };

        public Task<Catalog> LoadAsync(string location, CancellationToken ct = default) =>
            Fail is null ? Task.FromResult(new Catalog { Generated = DateTime.UtcNow, Packages = [.. _entries.Values] }) : throw new RemoteException(Fail);

        public Task DownloadAsync(string url, string path, CancellationToken ct = default)
        {
            File.WriteAllBytes(path, Corrupt ? [.. Content(url), 0] : Content(url));
            return Task.CompletedTask;
        }
    }
}
