using System.Security.Cryptography;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <param name="Package">Das importierte Paket; null, wenn der Eintrag noch nicht importiert ist.</param>
public sealed record CatalogItem(CatalogEntry Entry, Package? Package)
{
    public bool UpdateAvailable => Package is not null && VersionComparer.Compare(Package.Version, Entry.Version) < 0;
}

/// <summary>
/// Paketbibliothek: fertige Pakete aus einem regelmäßig neu erzeugten Katalog. Beim Import lädt der Server das Setup
/// vom Hersteller, prüft die Prüfsumme aus dem Katalog und legt ein gewöhnliches Paket an, das sich aktuell halten lässt.
/// </summary>
public sealed class CatalogService(IDbContextFactory<InventoryDbContext> dbFactory, ICatalogSource source, PackageService packages,
    CollectionService collections, AuditService audit, TimeProvider time, string catalogLocation)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Location => catalogLocation;
    public Catalog? Current { get; private set; }
    public DateTime? LoadedUtc { get; private set; }
    public string? LastError { get; private set; }

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

    public async Task<List<CatalogItem>> ListAsync(CancellationToken ct = default)
    {
        await RefreshAsync(false, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var imported = await db.Packages.AsNoTracking().Where(p => p.CatalogId != null).ToListAsync(ct);
        return (Current?.Packages ?? [])
            .Select(e => new CatalogItem(e, imported.FirstOrDefault(p => p.CatalogId == e.Id)))
            .OrderBy(i => i.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Legt aus einem Katalogeintrag ein Paket an (Setup laden, prüfen, Installationsschritt erzeugen).</summary>
    /// <exception cref="RemoteException">Download fehlgeschlagen oder Prüfsumme falsch.</exception>
    /// <exception cref="InvalidOperationException">Eintrag unbekannt oder bereits importiert.</exception>
    public async Task<Package> ImportAsync(string catalogId, bool autoUpdate, bool createCollection, string user, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entry = Current?.Packages.FirstOrDefault(e => e.Id == catalogId) ?? throw new InvalidOperationException("Dieser Eintrag ist nicht (mehr) in der Bibliothek.");
            string name;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (await db.Packages.AnyAsync(p => p.CatalogId == catalogId, ct))
                    throw new InvalidOperationException($"{entry.Name} ist bereits importiert.");
                // Ein selbst angelegtes Paket gleichen Namens bleibt unangetastet.
                name = await db.Packages.AnyAsync(p => p.Name == entry.Name, ct) ? $"{entry.Name} (Bibliothek)" : entry.Name;
            }

            var package = await packages.SaveAsync(null, name, entry.Version, Describe(entry), ct);
            try
            {
                await DownloadVerifiedAsync(package.Id, entry, ct);
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
                var collectionName = $"{name} veraltet";
                var existing = (await collections.ListAsync(ct)).Any(c => c.Collection.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase));
                if (!existing)
                    collectionId = (await collections.SaveAsync(null, collectionName, $"Rechner mit {entry.Match} älter als die Bibliotheksversion", true, true, [OutdatedRule(entry)], ct)).Id;
            }

            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.CatalogId, catalogId)
                    .SetProperty(p => p.CatalogAutoUpdate, autoUpdate)
                    .SetProperty(p => p.CatalogCollectionId, collectionId), ct);
            }
            await audit.LogAsync(user, "Paket aus Bibliothek importiert", $"{name} {entry.Version}", ct);
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
            var entry = Current?.Packages.FirstOrDefault(e => e.Id == package?.CatalogId);
            if (package is null || entry is null || VersionComparer.Compare(package.Version, entry.Version) >= 0)
                return false;

            var previous = package.Version;
            var oldFiles = packages.Repository.List(packageId).Select(f => f.RelativePath).ToList();
            await DownloadVerifiedAsync(packageId, entry, ct);

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

            await audit.LogAsync(user, "Paket aus Bibliothek aktualisiert", $"{package.Name}: {previous} → {entry.Version}", ct);
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
                if (await UpdateAsync(item.Package!.Id, "(automatisch)", ct))
                    updated++;
            }
            catch (RemoteException ex)
            {
                await audit.LogAsync("(automatisch)", "Paket-Aktualisierung fehlgeschlagen", $"{item.Package!.Name}: {ex.Message}", ct);
            }
        }
        return updated;
    }

    /// <summary>Setups aus der Bibliothek heißen immer "ID-Version.Endung"; nur solche werden beim Aktualisieren ersetzt.</summary>
    private static bool IsCatalogFile(string fileName, string catalogId) => fileName.StartsWith(catalogId + "-", StringComparison.OrdinalIgnoreCase);

    private async Task DownloadVerifiedAsync(int packageId, CatalogEntry entry, CancellationToken ct)
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
                throw new RemoteException($"Die Prüfsumme des Setups von {entry.Name} stimmt nicht mit der Bibliothek überein. " +
                                          "Vermutlich hat der Hersteller die Datei bereits durch eine neuere ersetzt; nach dem nächsten Katalog-Lauf erneut versuchen.");
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static JobStep InstallStep(CatalogEntry entry) => new()
    {
        Name = $"{entry.Name} installieren",
        Type = StepType.Install,
        File = Path.GetFileName(entry.FileName),
        Arguments = entry.Arguments,
        SuccessCodes = entry.SuccessCodes,
        TimeoutSeconds = 3600,
    };

    private static CollectionRule OutdatedRule(CatalogEntry entry) =>
        new() { Field = RuleField.Software, Operator = RuleOperator.Contains, Value = entry.Match, Value2 = entry.Version };

    private static string Describe(CatalogEntry entry) =>
        string.Join(" – ", new[] { entry.Publisher, entry.Description }.Where(t => !string.IsNullOrWhiteSpace(t)));
}
