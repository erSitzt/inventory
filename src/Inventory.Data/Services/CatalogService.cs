using System.Security.Cryptography;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="Package">Das importierte Paket; null, wenn der Eintrag noch nicht importiert ist.</param>
/// <param name="Curated">false bei Einträgen, die nicht zur Grundauswahl gehören, sondern aus dem Suchindex über ganz winget stammen.</param>
public sealed record CatalogItem(CatalogEntry Entry, Package? Package, bool Curated = true)
{
    public bool UpdateAvailable => Package is not null && VersionComparer.Compare(Package.Version, Entry.Version) < 0;
}

/// <summary>
/// Paketbibliothek: fertige Pakete aus einem regelmäßig neu erzeugten Katalog. Beim Import lädt der Server das Setup
/// vom Hersteller, prüft die Prüfsumme aus dem Katalog und legt ein gewöhnliches Paket an, das sich aktuell halten lässt.
/// </summary>
public sealed class CatalogService(IDbContextFactory<InventoryDbContext> dbFactory, ICatalogSource source, PackageService packages,
    CollectionService collections, AuditService audit, TimeProvider time, string catalogLocation, string? indexLocation = null, string? versionsLocation = null, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan IndexRefreshInterval = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Location => catalogLocation;
    public Catalog? Current { get; private set; }
    public DateTime? LoadedUtc { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Suchindex über das gesamte winget-Repository; wird erst geladen, wenn jemand sucht oder ein Paket daraus stammt.</summary>
    public Catalog? Index { get; private set; }
    public DateTime? IndexLoadedUtc { get; private set; }
    public string? IndexError { get; private set; }
    public bool IndexAvailable => !string.IsNullOrWhiteSpace(indexLocation);

    /// <summary>Lädt den Katalog; ohne <paramref name="force"/> höchstens alle paar Stunden neu.</summary>
    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (!force && LoadedUtc is { } loaded && now - loaded < RefreshInterval)
            return;
        LoadedUtc = now;
        try
        {
            Current = await source.LoadAsync(catalogLocation, ct);
            LastError = null;
        }
        catch (RemoteException ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Lädt den Suchindex; ohne <paramref name="force"/> höchstens einmal am Tag neu.</summary>
    public async Task RefreshIndexAsync(bool force = false, CancellationToken ct = default)
    {
        if (!IndexAvailable)
            return;
        await _indexLock.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (!force && IndexLoadedUtc is { } loaded && now - loaded < IndexRefreshInterval)
                return;
            IndexLoadedUtc = now;
            try
            {
                Index = await source.LoadAsync(indexLocation!, ct);
                IndexError = null;
            }
            catch (RemoteException ex)
            {
                IndexError = ex.Message;
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>Die Grundauswahl, dazu alle bereits importierten Pakete aus dem Suchindex.</summary>
    public async Task<List<CatalogItem>> ListAsync(CancellationToken ct = default)
    {
        await RefreshAsync(false, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var imported = await db.Packages.AsNoTracking().Where(p => p.CatalogId != null).ToListAsync(ct);
        var curated = Current?.Packages ?? [];
        var items = curated.Select(e => new CatalogItem(e, imported.FirstOrDefault(p => p.CatalogId == e.Id))).ToList();

        var others = imported.Where(p => curated.All(e => e.Id != p.CatalogId)).ToList();
        if (others.Count > 0)
        {
            await RefreshIndexAsync(false, ct);
            foreach (var package in others)
            {
                if (Index?.Packages.FirstOrDefault(e => e.Id == package.CatalogId) is { } entry)
                    items.Add(new CatalogItem(entry, package, Curated: false));
            }
        }
        return items.OrderBy(i => i.Entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Sucht im Index über ganz winget nach Programmen, die nicht zur Grundauswahl gehören und noch nicht importiert sind.</summary>
    public async Task<List<CatalogItem>> SearchAsync(string text, int limit = 50, CancellationToken ct = default)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Sum(w => w.Length) < 2)
            return [];
        await RefreshAsync(false, ct);
        await RefreshIndexAsync(false, ct);
        var known = (Current?.Packages ?? []).Select(e => e.Id).ToHashSet();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            known.UnionWith(await db.Packages.Where(p => p.CatalogId != null).Select(p => p.CatalogId!).ToListAsync(ct));

        return (Index?.Packages ?? [])
            .Where(e => !known.Contains(e.Id) && words.All(w => Matches(e, w)))
            // Treffer im Namen zuerst, kurze Namen vor langen: "Firefox" soll vor den vielen Sprachfassungen stehen.
            .OrderBy(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(e => e.Name.Length)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(e => new CatalogItem(e, null, Curated: false))
            .ToList();
    }

    private static bool Matches(CatalogEntry entry, string word) =>
        entry.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
        || entry.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
        || (entry.Publisher?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
        || (entry.Moniker?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
        || entry.Tags.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));

    public bool VersionsAvailable => !string.IsNullOrWhiteSpace(versionsLocation);

    /// <summary>
    /// Alle Versionen eines Programms, die das winget-Repository führt, die neueste zuerst. Die Angaben zum Programm
    /// (Name, Hersteller, Erkennung) stammen vom aktuellen Eintrag, Setup und Prüfsumme von der jeweiligen Version.
    /// </summary>
    /// <exception cref="RemoteException">Die Versionsliste ist nicht erreichbar.</exception>
    public async Task<List<CatalogEntry>> VersionsAsync(string catalogId, CancellationToken ct = default)
    {
        var current = Find(catalogId) ?? throw new InvalidOperationException(L["This entry is not (anymore) in the library."]);
        // Die Kennung wird Teil einer Adresse bzw. eines Pfades.
        if (!VersionsAvailable || !System.Text.RegularExpressions.Regex.IsMatch(catalogId, @"^[A-Za-z0-9][A-Za-z0-9.+_-]*$"))
            return [current];
        var folder = versionsLocation!.TrimEnd('/', '\\');
        var location = folder.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? $"{folder}/{Uri.EscapeDataString(catalogId)}.json" : Path.Combine(folder, catalogId + ".json");
        var versions = (await source.LoadAsync(location, ct)).Packages
            .Where(v => v.Id == catalogId && v.Version != current.Version)
            .Select(v => new CatalogEntry
            {
                Id = current.Id, Name = current.Name, Publisher = current.Publisher, Description = current.Description, Homepage = current.Homepage,
                License = current.License, LicenseUrl = current.LicenseUrl, Match = current.Match,
                Version = v.Version, ReleaseDate = v.ReleaseDate, InstallerType = v.InstallerType, Architecture = v.Architecture, Scope = v.Scope,
                Url = v.Url, Sha256 = v.Sha256, FileName = v.FileName, Arguments = v.Arguments, SuccessCodes = v.SuccessCodes,
            });
        return [current, .. versions];
    }

    /// <summary>Die Grundauswahl hat Vorrang vor dem Suchindex.</summary>
    private CatalogEntry? Find(string? catalogId) =>
        catalogId is null ? null : Current?.Packages.FirstOrDefault(e => e.Id == catalogId) ?? Index?.Packages.FirstOrDefault(e => e.Id == catalogId);

    /// <summary>Legt aus einem Katalogeintrag ein Paket an (Setup laden, prüfen, Installationsschritt erzeugen).</summary>
    /// <exception cref="RemoteException">Download fehlgeschlagen oder Prüfsumme falsch.</exception>
    /// <exception cref="InvalidOperationException">Eintrag unbekannt oder bereits importiert.</exception>
    /// <param name="version">
    /// Eine bestimmte ältere Version; null = die aktuelle. Ein Paket mit fester Version ist von der Bibliothek gelöst:
    /// es heißt "Name Version", wird nie automatisch aktualisiert und darf neben dem aktuellen Paket bestehen.
    /// </param>
    public async Task<Package> ImportAsync(string catalogId, bool autoUpdate, bool createCollection, string user, string? version = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entry = Find(catalogId) ?? throw new InvalidOperationException(L["This entry is not (anymore) in the library."]);
            var pinned = version is not null && version != entry.Version;
            if (pinned)
                entry = (await VersionsAsync(catalogId, ct)).FirstOrDefault(v => v.Version == version)
                        ?? throw new InvalidOperationException(L["{0} {1} is not (anymore) listed in the winget repository.", entry.Name, version]);
            string name;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (pinned)
                {
                    name = $"{entry.Name} {entry.Version}";
                    if (await db.Packages.AnyAsync(p => p.Name == name, ct))
                        throw new InvalidOperationException($"Ein Paket „{name}“ gibt es bereits.");
                }
                else
                {
                    if (await db.Packages.AnyAsync(p => p.CatalogId == catalogId, ct))
                        throw new InvalidOperationException(L["{0} is already imported.", entry.Name]);
                    // Ein selbst angelegtes Paket gleichen Namens bleibt unangetastet.
                    name = await db.Packages.AnyAsync(p => p.Name == entry.Name, ct) ? $"{entry.Name} (Bibliothek)" : entry.Name;
                }
            }

            var package = await packages.SaveAsync(null, name, entry.Version, Describe(entry), ct);
            try
            {
                await DownloadCuratedAsync(package.Id, entry, ct);
            }
            catch
            {
                await packages.DeleteAsync(package.Id, CancellationToken.None);
                throw;
            }
            await packages.SaveStepAsync(package.Id, null, InstallStep(entry), null, ct);

            int? collectionId = null;
            if (createCollection)
            {
                var collectionName = L["{0} outdated", name].Value;
                var existing = (await collections.ListAsync(ct)).Any(c => c.Collection.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase));
                if (!existing)
                    collectionId = (await collections.SaveAsync(null, collectionName, L["Computers with {0} older than the library version", entry.Match], true, true, [OutdatedRule(entry)], ct)).Id;
            }

            if (!pinned)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.CatalogId, catalogId)
                    .SetProperty(p => p.CatalogAutoUpdate, autoUpdate)
                    .SetProperty(p => p.CatalogCollectionId, collectionId), ct);
            }
            await audit.LogAsync(user, L["Package imported from library"], pinned ? $"{name} (feste Version)" : $"{name} {entry.Version}", ct);
            return (await packages.GetAsync(package.Id, ct))!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Hebt ein importiertes Paket auf die Version der Bibliothek: neues Setup, angepasster Schritt, mitwandernde Sammlung.</summary>
    /// <exception cref="RemoteException">Download fehlgeschlagen oder Prüfsumme falsch; das Paket bleibt unverändert.</exception>
    public async Task<bool> UpdateAsync(int packageId, string user, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var package = await packages.GetAsync(packageId, ct);
            var entry = Find(package?.CatalogId);
            if (package is null || entry is null || VersionComparer.Compare(package.Version, entry.Version) >= 0)
                return false;

            var previous = package.Version;
            var oldFiles = packages.Repository.List(packageId).Select(f => f.RelativePath).ToList();
            await DownloadCuratedAsync(packageId, entry, ct);

            // Den Schritt anpassen, der bisher das alte Setup installiert hat; eigene Zusatzschritte bleiben erhalten.
            var step = package.Steps.FirstOrDefault(s => s.NestedPackageId is null && s.Definition.Type == StepType.Install && oldFiles.Contains(s.Definition.File ?? ""));
            if (step is not null)
            {
                var definition = step.Definition;
                definition.File = entry.FileName;
                definition.Arguments = entry.Arguments;
                definition.SuccessCodes = entry.SuccessCodes;
                await packages.SaveStepAsync(packageId, step.Id, definition, null, ct);
            }
            foreach (var old in oldFiles.Where(f => !f.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase) && IsCatalogFile(f, package.CatalogId!)))
                packages.Repository.Delete(packageId, old);

            await packages.SaveAsync(packageId, package.Name, entry.Version, Describe(entry), ct);
            if (package.CatalogCollectionId is { } collectionId && await collections.GetAsync(collectionId, ct) is { } collection)
                await collections.SaveAsync(collectionId, collection.Name, collection.Description, true, collection.MatchAll, [OutdatedRule(entry)], ct);

            await audit.LogAsync(user, L["Package updated from library"], $"{package.Name}: {previous} → {entry.Version}", ct);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAutoUpdateAsync(int packageId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Packages.Where(p => p.Id == packageId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CatalogAutoUpdate, enabled), ct);
    }

    /// <summary>Aktualisiert alle Pakete, für die das automatisch geschehen soll; liefert die Zahl der aktualisierten.</summary>
    public async Task<int> UpdateDueAsync(CancellationToken ct = default)
    {
        await RefreshAsync(false, ct);
        var updated = 0;
        foreach (var item in (await ListAsync(ct)).Where(i => i is { UpdateAvailable: true, Package.CatalogAutoUpdate: true }))
        {
            try
            {
                if (await UpdateAsync(item.Package!.Id, L["(automatic)"], ct))
                    updated++;
            }
            catch (RemoteException ex)
            {
                await audit.LogAsync(L["(automatic)"], L["Package update failed"], $"{item.Package!.Name}: {ex.Message}", ct);
            }
        }
        return updated;
    }

    /// <summary>Setups aus der Bibliothek heißen immer "ID-Version.Endung"; nur solche werden beim Aktualisieren ersetzt.</summary>
    private static bool IsCatalogFile(string fileName, string catalogId) => fileName.StartsWith(catalogId + "-", StringComparison.OrdinalIgnoreCase);

    private async Task DownloadCuratedAsync(int packageId, CatalogEntry entry, CancellationToken ct)
    {
        // Nur der Dateiname: ein präparierter Katalogeintrag darf nicht aus dem Paketordner herausführen.
        var fileName = Path.GetFileName(entry.FileName);
        var target = packages.Repository.Resolve(packageId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".download";
        try
        {
            await source.DownloadAsync(entry.Url, temp, ct);
            string actual;
            await using (var stream = File.OpenRead(temp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new RemoteException(L["The checksum of the setup for {0} does not match the library. The vendor has probably already replaced the file with a newer one; try again after the next catalog run.", entry.Name]);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private JobStep InstallStep(CatalogEntry entry) => new()
    {
        Name = L["Install {0}", entry.Name],
        Type = StepType.Install,
        File = Path.GetFileName(entry.FileName),
        Arguments = entry.Arguments,
        SuccessCodes = entry.SuccessCodes,
        TimeoutSeconds = 3600,
        // Ein Setup, das sich nur ins Benutzerprofil installiert, wäre als SYSTEM wirkungslos.
        RunAs = entry.Scope == "user" ? StepRunAs.LoggedOnUser : StepRunAs.System,
    };

    private static CollectionRule OutdatedRule(CatalogEntry entry) =>
        new() { Field = RuleField.Software, Operator = RuleOperator.Contains, Value = entry.Match, Value2 = entry.Version };

    private static string Describe(CatalogEntry entry) =>
        string.Join(" – ", new[] { entry.Publisher, entry.Description }.Where(t => !string.IsNullOrWhiteSpace(t)));
}
