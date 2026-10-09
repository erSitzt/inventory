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
        _catalog = new CatalogService(_db, _source, _packages, _collections, new AuditService(_db), TimeProvider.System, "test", "index", "versions");
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

    [Fact]
    public async Task Search_finds_programs_outside_the_base_selection_and_imports_them()
    {
        _source.PublishIndex("7zip.7zip", "7-Zip", "24.08");
        _source.Publish("7zip.7zip", "7-Zip", "24.08", "");
        _source.PublishIndex("Vendor.ZipTool", "Zip Tool Pro", "2.0", tags: ["archive"]);
        _source.PublishIndex("Vendor.Chat", "Chat", "1.0", scope: "user");
        await _catalog.RefreshAsync();

        // Was schon in der Grundauswahl steht, taucht in der Suche nicht noch einmal auf.
        Assert.Equal(["Vendor.ZipTool"], (await _catalog.SearchAsync("zip")).Select(i => i.Entry.Id));
        Assert.Equal(["Vendor.ZipTool"], (await _catalog.SearchAsync("ARCHIVE")).Select(i => i.Entry.Id));
        Assert.Equal(["Vendor.ZipTool"], (await _catalog.SearchAsync("tool vendor")).Select(i => i.Entry.Id));
        Assert.Empty(await _catalog.SearchAsync("z"));
        Assert.False((await _catalog.SearchAsync("chat")).Single().Curated);

        var package = await _catalog.ImportAsync("Vendor.Chat", autoUpdate: true, createCollection: false, "anna");
        // Ein Setup nur fürs Benutzerprofil läuft im Kontext des angemeldeten Benutzers.
        Assert.Equal(StepRunAs.LoggedOnUser, Assert.Single(package.Steps).Definition.RunAs);
        Assert.Empty(await _catalog.SearchAsync("chat"));
        var listed = (await _catalog.ListAsync()).Single(i => i.Entry.Id == "Vendor.Chat");
        Assert.Equal((false, false), (listed.Curated, listed.UpdateAvailable));

        _source.PublishIndex("Vendor.Chat", "Chat", "1.1", scope: "user");
        await _catalog.RefreshIndexAsync(force: true);
        Assert.Equal(1, await _catalog.UpdateDueAsync());
        Assert.Equal("1.1", (await _packages.GetAsync(package.Id))!.Version);
    }

    [Fact]
    public async Task An_older_version_becomes_a_separate_package_that_is_never_updated()
    {
        _source.Publish("7zip.7zip", "7-Zip", "24.08", "");
        _source.PublishVersions("7zip.7zip", "24.08", "23.01", "22.01");
        await _catalog.RefreshAsync();

        var versions = await _catalog.VersionsAsync("7zip.7zip");
        Assert.Equal(["24.08", "23.01", "22.01"], versions.Select(v => v.Version));
        // Name und Erkennung stammen vom aktuellen Eintrag, das Setup von der Version.
        Assert.Equal(("7-Zip", "7-Zip", "7zip.7zip-23.01.msi"), (versions[1].Name, versions[1].Match, versions[1].FileName));

        var old = await _catalog.ImportAsync("7zip.7zip", autoUpdate: true, createCollection: true, "anna", version: "23.01");
        Assert.Equal(("7-Zip 23.01", "23.01", null, false), (old.Name, old.Version, old.CatalogId, old.CatalogAutoUpdate));
        Assert.Equal("7zip.7zip-23.01.msi", Assert.Single(_packages.Repository.List(old.Id)).RelativePath);
        Assert.Contains(await _collections.ListAsync(), c => c.Collection.Name == "7-Zip 23.01 veraltet");

        // Das aktuelle Paket lässt sich daneben importieren; die feste Version bleibt bei Aktualisierungen unberührt.
        Assert.Null((await _catalog.ListAsync()).Single().Package);
        var current = await _catalog.ImportAsync("7zip.7zip", autoUpdate: true, createCollection: false, "anna");
        _source.Publish("7zip.7zip", "7-Zip", "25.01", "");
        await _catalog.RefreshAsync(force: true);
        Assert.Equal(1, await _catalog.UpdateDueAsync());
        Assert.Equal("25.01", (await _packages.GetAsync(current.Id))!.Version);
        Assert.Equal("23.01", (await _packages.GetAsync(old.Id))!.Version);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.ImportAsync("7zip.7zip", false, false, "anna", version: "23.01"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.ImportAsync("7zip.7zip", false, false, "anna", version: "1.0"));
    }

    [Fact]
    public async Task Search_reports_an_unreachable_index_without_breaking_the_list()
    {
        _source.Publish("7zip.7zip", "7-Zip", "24.08", "");
        await _catalog.RefreshAsync();
        _source.FailIndex = "nicht erreichbar";

        Assert.Empty(await _catalog.SearchAsync("zip"));
        Assert.Equal("nicht erreichbar", _catalog.IndexError);
        Assert.Single(await _catalog.ListAsync());
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

        private readonly Dictionary<string, CatalogEntry> _index = [];
        public string? FailIndex { get; set; }

        public void PublishIndex(string id, string name, string version, string? scope = "machine", string[]? tags = null)
        {
            Publish(id, name, version, "");
            _index[id] = _entries[id];
            _index[id].Scope = scope;
            _index[id].Tags = [.. tags ?? []];
            _entries.Remove(id);
        }

        private readonly Dictionary<string, List<CatalogEntry>> _versions = [];

        public void PublishVersions(string id, params string[] versions) => _versions[id] = [.. versions.Select(version => new CatalogEntry
        {
            Id = id, Version = version, InstallerType = "wix", Url = version, Sha256 = Convert.ToHexString(SHA256.HashData(Content(version))).ToLowerInvariant(),
            FileName = $"{id}-{version}.msi", SuccessCodes = [0, 1641, 3010],
        })];

        public Task<Catalog> LoadAsync(string location, CancellationToken ct = default) =>
            location.StartsWith("versions") ? Task.FromResult(new Catalog { Packages = _versions.GetValueOrDefault(Path.GetFileNameWithoutExtension(location), []) }) :
            location == "index" ? FailIndex is null ? Task.FromResult(new Catalog { Packages = [.. _index.Values] }) : throw new RemoteException(FailIndex) :
            Fail is null ? Task.FromResult(new Catalog { Generated = DateTime.UtcNow, Packages = [.. _entries.Values] }) : throw new RemoteException(Fail);

        public Task DownloadAsync(string url, string path, CancellationToken ct = default)
        {
            File.WriteAllBytes(path, Corrupt ? [.. Content(url), 0] : Content(url));
            return Task.CompletedTask;
        }
    }
}
