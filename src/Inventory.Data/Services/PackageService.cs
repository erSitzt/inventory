using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="Title">Name des Pakets, wie ihn der Benutzer zu sehen bekommt.</param>
public sealed record UserNoticeOptions(string Title, int MaxDeferrals, int DeferMinutes);

/// <param name="PackageIds">Alle Pakete, deren Schritte und Dateien im Job stecken: das verteilte und die eingebetteten.</param>
public sealed record BuiltJob(JobManifest Manifest, List<JobFile> Files, IReadOnlyCollection<int> PackageIds)
{
    /// <summary>Gesetzt = das Paket fragt den angemeldeten Benutzer vor dem Start.</summary>
    public UserNoticeOptions? Notice { get; init; }

    /// <summary>Job ohne Paket dahinter, z. B. die Installation des Agenten.</summary>
    public BuiltJob(JobManifest manifest, List<JobFile> files) : this(manifest, files, []) { }

    /// <summary>Nur vom <see cref="AgentService"/> gesetzt: dieser Job darf auch dann agentenlos laufen, wenn sonst nur der Agent erlaubt ist.</summary>
    public bool InstallsAgent { get; internal init; }
}

public sealed class PackageService(IDbContextFactory<InventoryDbContext> dbFactory, PackageRepository repository, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    /// <summary>So tief dürfen Pakete ineinander verschachtelt sein; fängt zugleich Zyklen ab.</summary>
    private const int MaxNestingDepth = 5;

    /// <summary>
    /// Änderungen am Geflecht aus Voraussetzungen und eingebetteten Paketen laufen nacheinander: sonst sähen zwei gleichzeitige
    /// Änderungen (A braucht B, B braucht A) jeweils noch keinen Zyklus, und ein Löschen verpasste eine eben erst angelegte Voraussetzung.
    /// </summary>
    private static readonly SemaphoreSlim GraphLock = new(1, 1);

    public PackageRepository Repository => repository;

    public async Task<List<Package>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Packages.AsNoTracking().Include(p => p.Steps).OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<Package?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Packages.AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Steps.OrderBy(s => s.Order)).ThenInclude(s => s.NestedPackage)
            .Include(p => p.Dependencies.OrderBy(d => d.Order)).ThenInclude(d => d.RequiredPackage)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>Hinweis an den Benutzer vor dem Start. Ändert nichts an dem, was das Paket ausführt: die Freigabe bleibt.</summary>
    public async Task SaveNoticeAsync(int id, bool askUser, int maxDeferrals, int deferMinutes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.Packages.FindAsync([id], ct) ?? throw new KeyNotFoundException(L["Package {0} not found", id]);
        package.AskUser = askUser;
        package.MaxDeferrals = Math.Clamp(maxDeferrals, 0, UserNotice.MaxDeferrals);
        package.DeferMinutes = Math.Clamp(deferMinutes, UserNotice.MinMinutes, UserNotice.MaxMinutes);
        package.ModifiedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Prüft Voraussetzungen, ohne etwas zu speichern: jedes Paket muss existieren und darf dieses nicht selbst enthalten.</summary>
    public async Task CheckDependenciesAsync(int packageId, IReadOnlyList<int> requiredPackageIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await CheckDependenciesAsync(db, packageId, requiredPackageIds, ct);
    }

    private async Task CheckDependenciesAsync(InventoryDbContext db, int packageId, IReadOnlyList<int> requiredPackageIds, CancellationToken ct)
    {
        foreach (var id in requiredPackageIds.Distinct())
        {
            if (!await db.Packages.AnyAsync(p => p.Id == id, ct))
                throw new KeyNotFoundException(L["Package {0} not found", id]);
            if (await ContainsAsync(db, id, packageId, MaxNestingDepth, ct))
                throw new InvalidOperationException(L["This package would then contain itself."]);
        }
    }

    /// <summary>Ersetzt die Voraussetzungen des Pakets; die Reihenfolge der Liste ist die der Ausführung.</summary>
    public async Task SetDependenciesAsync(int packageId, IReadOnlyList<int> requiredPackageIds, CancellationToken ct = default)
    {
        await GraphLock.WaitAsync(ct);
        try
        {
            await SetDependenciesCoreAsync(packageId, requiredPackageIds, ct);
        }
        finally
        {
            GraphLock.Release();
        }
    }

    private async Task SetDependenciesCoreAsync(int packageId, IReadOnlyList<int> requiredPackageIds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wanted = requiredPackageIds.Distinct().ToList();
        var current = await db.PackageDependencies.Where(d => d.PackageId == packageId).OrderBy(d => d.Order).ToListAsync(ct);
        if (current.Select(d => d.RequiredPackageId).SequenceEqual(wanted))
            return;
        await CheckDependenciesAsync(db, packageId, wanted, ct);
        // Eine Transaktion: niemand sieht das Paket zwischendurch ohne Voraussetzungen.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.PackageDependencies.RemoveRange(current);
        // Erst entfernen, dann neu anlegen: der eindeutige Index gilt je Paar.
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < wanted.Count; i++)
            db.PackageDependencies.Add(new PackageDependency { PackageId = packageId, RequiredPackageId = wanted[i], Order = i + 1 });
        await TouchAsync(db, packageId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<Package> SaveAsync(int? id, string name, string? version, string? description, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Package package;
        if (id is null)
        {
            package = new Package { Name = name.Trim() };
            db.Packages.Add(package);
        }
        else
        {
            package = await db.Packages.FindAsync([id.Value], ct) ?? throw new KeyNotFoundException(L["Package {0} not found", id]);
            package.Name = name.Trim();
        }
        package.Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        package.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        package.ModifiedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return package;
    }

    /// <summary>Gibt das Paket zum Verteilen durch Operatoren und Zeitpläne frei.</summary>
    /// <exception cref="InvalidOperationException">Ein Paket ohne Schritte lässt sich nicht freigeben.</exception>
    public async Task ApproveAsync(int id, string user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.Packages.Include(p => p.Steps).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException(L["Package {0} not found", id]);
        if (package.Steps.Count == 0)
            throw new InvalidOperationException(L["The package contains no steps."]);
        package.ApprovedBy = user;
        package.ApprovedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hebt die Freigabe auf, z. B. weil sich Dateien des Pakets geändert haben.</summary>
    public async Task RevokeApprovalAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Packages.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ApprovedBy, (string?)null).SetProperty(p => p.ApprovedUtc, (DateTime?)null), ct);
    }

    /// <summary>Legt fest, woran ein bereits installiertes Paket erkannt wird; eine leere Erkennung schaltet sie ab.</summary>
    public async Task SaveDetectionAsync(int id, StepConditions? detection, bool verifyAfterInstall, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.Packages.FindAsync([id], ct) ?? throw new KeyNotFoundException(L["Package {0} not found", id]);
        var before = (package.Detection is null ? "" : JobJson.Serialize(package.Detection), package.VerifyAfterInstall);
        package.Detection = detection is null or { IsEmpty: true } ? null : detection;
        // Ohne Erkennung gäbe es nichts zu prüfen.
        package.VerifyAfterInstall = verifyAfterInstall && package.Detection is not null;
        // Nur eine wirkliche Änderung hebt die Freigabe auf – ein Speichern ohne Unterschied (z. B. über die API zusammen mit dem Namen) nicht.
        if (before != (package.Detection is null ? "" : JobJson.Serialize(package.Detection), package.VerifyAfterInstall))
        {
            package.ModifiedUtc = DateTime.UtcNow;
            package.ApprovedBy = null;
            package.ApprovedUtc = null;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Liefert das Paket "Uninstall &lt;Name&gt;" mit einem Deinstallations-Schritt und legt es an, falls es noch fehlt.
    /// Ein vorhandenes Paket dieses Namens bleibt unverändert, damit eigene Anpassungen (Parameter, weitere Schritte) erhalten bleiben.
    /// </summary>
    /// <param name="approvedBy">Gesetzt = ein neu angelegtes Paket ist sofort freigegeben (der Administrator hat es gerade bewusst erzeugt).</param>
    public async Task<(Package Package, bool Created)> EnsureUninstallPackageAsync(string label, string? softwareName, string? productCode, CancellationToken ct = default, string? approvedBy = null)
    {
        var name = L["Uninstall {0}", label.Trim()].Value;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name, ct) is { } existing)
                return (existing, false);
        }
        var package = await SaveAsync(null, name, null, null, ct);
        await SaveStepAsync(package.Id, null, new JobStep
        {
            Name = name, Type = StepType.Uninstall, TimeoutSeconds = 3600,
            SoftwareName = string.IsNullOrWhiteSpace(softwareName) ? null : softwareName.Trim(),
            ProductCode = string.IsNullOrWhiteSpace(productCode) ? null : productCode.Trim(),
        }, null, ct);
        if (approvedBy is not null)
            await ApproveAsync(package.Id, approvedBy, ct);
        return (package, true);
    }

    /// <summary>Paket, das genau diese Updates installiert; wird beim ersten Aufruf angelegt und danach wiederverwendet.</summary>
    /// <param name="kbIds">KB-Nummern wie in <see cref="ComputerPendingUpdate.KbIds"/>, durch Komma getrennt.</param>
    public async Task<(Package Package, bool Created)> EnsureUpdatePackageAsync(string kbIds, CancellationToken ct = default, string? approvedBy = null)
    {
        var ids = PendingUpdates.NormalizeKbIds(kbIds);
        if (ids.Count == 0)
            throw new InvalidOperationException(L["This update has no KB number and cannot be installed on its own."]);
        var name = L["Install update {0}", string.Join(", ", ids)].Value;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Wiederverwendet wird, was genau diese Updates installiert, nicht was so heißt: der Name hängt von der Sprache ab
            // und bleibt stehen, wenn jemand den Schritt ändert.
            var candidates = await db.Packages.AsNoTracking().Include(p => p.Steps).Where(p => p.Steps.Count == 1).ToListAsync(ct);
            if (candidates.FirstOrDefault(p => p.Steps[0] is { NestedPackageId: null, Definition: { Type: StepType.WindowsUpdate } step }
                                               && step.UpdateKbIds.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(ids.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) is { } existing)
                return (existing, false);
            if (await db.Packages.AnyAsync(p => p.Name == name, ct))
                name = $"{name} ({DateTime.Now:yyyy-MM-dd HH:mm})";
        }
        var package = await SaveAsync(null, name, null, null, ct);
        await SaveStepAsync(package.Id, null, new JobStep { Name = name, Type = StepType.WindowsUpdate, TimeoutSeconds = 7200, UpdateKbIds = ids }, null, ct);
        if (approvedBy is not null)
            await ApproveAsync(package.Id, approvedBy, ct);
        return (package, true);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await GraphLock.WaitAsync(ct);
        try
        {
            await DeleteCoreAsync(id, ct);
        }
        finally
        {
            GraphLock.Release();
        }
    }

    private async Task DeleteCoreAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Pakete, die dieses voraussetzen oder einbetten, verlieren mit ihm einen Teil: sie sind nicht mehr das, was freigegeben wurde.
        var users = await db.PackageDependencies.Where(d => d.RequiredPackageId == id).Select(d => d.PackageId)
            .Union(db.PackageSteps.Where(s => s.NestedPackageId == id).Select(s => s.PackageId)).ToListAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var user in users.Where(user => user != id))
            await TouchAsync(db, user, ct);
        await db.SaveChangesAsync(ct);
        await db.Packages.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        repository.DeleteAll(id);
    }

    /// <summary>Legt einen Schritt an (stepId = null, ans Ende) oder ändert ihn.</summary>
    public async Task SaveStepAsync(int packageId, int? stepId, JobStep definition, int? nestedPackageId, CancellationToken ct = default)
    {
        // Nur ein eingebettetes Paket verändert das Geflecht; gewöhnliche Schritte warten nicht.
        if (nestedPackageId is null)
        {
            await SaveStepCoreAsync(packageId, stepId, definition, null, ct);
            return;
        }
        await GraphLock.WaitAsync(ct);
        try
        {
            await SaveStepCoreAsync(packageId, stepId, definition, nestedPackageId, ct);
        }
        finally
        {
            GraphLock.Release();
        }
    }

    private async Task SaveStepCoreAsync(int packageId, int? stepId, JobStep definition, int? nestedPackageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (nestedPackageId is not null && await ContainsAsync(db, nestedPackageId.Value, packageId, MaxNestingDepth, ct))
            throw new InvalidOperationException(L["This package would then contain itself."]);

        PackageStep step;
        if (stepId is null)
        {
            var last = await db.PackageSteps.Where(s => s.PackageId == packageId).MaxAsync(s => (int?)s.Order, ct) ?? 0;
            step = new PackageStep { PackageId = packageId, Order = last + 1 };
            db.PackageSteps.Add(step);
        }
        else
        {
            step = await db.PackageSteps.FirstAsync(s => s.Id == stepId && s.PackageId == packageId, ct);
        }
        // Auch über die API gelten die Grenzen des Dialogs; der Runner begrenzt zusätzlich selbst.
        definition.Retries = Math.Clamp(definition.Retries, 0, 20);
        definition.RetryDelaySeconds = Math.Clamp(definition.RetryDelaySeconds, 1, 3600);
        definition.CloseProcessesWarnSeconds = Math.Clamp(definition.CloseProcessesWarnSeconds, 0, 3600);
        definition.MinFreeDiskMb = Math.Max(definition.MinFreeDiskMb, 0);
        step.Definition = definition;
        step.NestedPackageId = nestedPackageId;
        await TouchAsync(db, packageId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteStepAsync(int packageId, int stepId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PackageSteps.Where(s => s.Id == stepId && s.PackageId == packageId).ExecuteDeleteAsync(ct);
        await TouchAsync(db, packageId, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Verschiebt einen Schritt um eine Position nach oben (-1) oder unten (+1).</summary>
    public async Task MoveStepAsync(int packageId, int stepId, int direction, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var steps = await db.PackageSteps.Where(s => s.PackageId == packageId).OrderBy(s => s.Order).ToListAsync(ct);
        var index = steps.FindIndex(s => s.Id == stepId);
        var other = index + Math.Sign(direction);
        if (index < 0 || other < 0 || other >= steps.Count)
            return;
        (steps[index], steps[other]) = (steps[other], steps[index]);
        for (var i = 0; i < steps.Count; i++)
            steps[i].Order = i + 1;
        await TouchAsync(db, packageId, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Baut aus einem Paket den Job für den Runner: eingebettete Pakete werden zu einer flachen Schrittfolge,
    /// und jedes beteiligte Paket bringt seinen kompletten Repository-Ordner als Unterordner p&lt;Id&gt; mit.
    /// </summary>
    public async Task<BuiltJob> BuildJobAsync(int packageId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var manifest = new JobManifest();
        var files = new List<JobFile>();
        var included = new HashSet<int>();
        await AppendAsync(db, packageId, prefix: "", manifest, files, included, MaxNestingDepth, group: null, ct);
        if (manifest.Steps.Count == 0)
            throw new InvalidOperationException(L["The package contains no steps."]);
        // Maßgeblich ist die Erkennung des verteilten Pakets; eingebettete Pakete bringen nur ihre Schritte mit.
        var header = await db.Packages.AsNoTracking().Where(p => p.Id == packageId)
            .Select(p => new { p.Name, p.Detection, p.VerifyAfterInstall, p.AskUser, p.MaxDeferrals, p.DeferMinutes }).FirstAsync(ct);
        manifest.Detection = header.Detection;
        manifest.VerifyAfterInstall = header.VerifyAfterInstall;
        return new BuiltJob(manifest, files, included)
        {
            Notice = header.AskUser ? new UserNoticeOptions(header.Name, header.MaxDeferrals, header.DeferMinutes) : null,
        };
    }

    /// <param name="group">Die Voraussetzung, zu der die Schritte gehören; null = Schritte des verteilten Pakets.</param>
    private async Task AppendAsync(InventoryDbContext db, int packageId, string prefix, JobManifest manifest, List<JobFile> files, HashSet<int> included, int depth, int? group, CancellationToken ct)
    {
        if (depth < 0)
            throw new InvalidOperationException(L["Packages are nested too deeply or in a cycle."]);
        var package = await db.Packages.AsNoTracking().AsSplitQuery()
                          .Include(p => p.Steps.OrderBy(s => s.Order))
                          .Include(p => p.Dependencies.OrderBy(d => d.Order)).ThenInclude(d => d.RequiredPackage)
                          .FirstOrDefaultAsync(p => p.Id == packageId, ct)
                      ?? throw new KeyNotFoundException(L["Package {0} not found", packageId]);

        // Voraussetzungen zuerst. Jede bildet eine Gruppe: der Runner prüft ihre Erkennung einmal vor ihrem ersten Schritt.
        foreach (var dependency in package.Dependencies)
        {
            var required = dependency.RequiredPackage!;
            var own = new JobGroup
            {
                Id = manifest.Groups.Count + 1, Name = required.Name, Parent = group,
                Detection = required.Detection, Verify = required.VerifyAfterInstall && required.Detection is not null,
            };
            manifest.Groups.Add(own);
            await AppendAsync(db, required.Id, $"{prefix}{required.Name} › ", manifest, files, included, depth - 1, own.Id, ct);
        }

        var folder = $"p{packageId}";
        if (included.Add(packageId))
            files.AddRange(repository.List(packageId).Select(f => new JobFile(repository.Resolve(packageId, f.RelativePath), $@"{folder}\{f.RelativePath}")));

        foreach (var step in package.Steps)
        {
            if (step.NestedPackageId is { } nestedId)
            {
                await AppendAsync(db, nestedId, $"{prefix}{step.Definition.Name} › ", manifest, files, included, depth - 1, group, ct);
                continue;
            }
            // Kopie, damit das Umschreiben der Pfade nicht auf das geladene Paket durchschlägt.
            var copy = JobJson.Deserialize<JobStep>(JobJson.Serialize(step.Definition))!;
            copy.Name = prefix + copy.Name;
            copy.Group = group;
            if (!string.IsNullOrWhiteSpace(copy.File))
                copy.File = $@"{folder}\{copy.File.TrimStart('\\', '/')}";
            manifest.Steps.Add(copy);
        }
    }

    /// <summary>true, wenn <paramref name="packageId"/> (direkt, über Verschachtelung oder Voraussetzungen) <paramref name="searchedId"/> enthält oder selbst ist.</summary>
    private static async Task<bool> ContainsAsync(InventoryDbContext db, int packageId, int searchedId, int depth, CancellationToken ct)
    {
        if (packageId == searchedId || depth < 0)
            return true;
        var nested = await db.PackageSteps.Where(s => s.PackageId == packageId && s.NestedPackageId != null).Select(s => s.NestedPackageId!.Value).ToListAsync(ct);
        nested.AddRange(await db.PackageDependencies.Where(d => d.PackageId == packageId).Select(d => d.RequiredPackageId).ToListAsync(ct));
        foreach (var id in nested)
        {
            if (await ContainsAsync(db, id, searchedId, depth - 1, ct))
                return true;
        }
        return false;
    }

    private static async Task TouchAsync(InventoryDbContext db, int packageId, CancellationToken ct)
    {
        var package = await db.Packages.FindAsync([packageId], ct);
        if (package is not null)
        {
            package.ModifiedUtc = DateTime.UtcNow;
            // Geänderte Schritte sind nicht mehr das, was freigegeben wurde.
            package.ApprovedBy = null;
            package.ApprovedUtc = null;
        }
    }
}
