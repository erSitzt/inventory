using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

public sealed record BuiltJob(JobManifest Manifest, List<JobFile> Files);

public sealed class PackageService(IDbContextFactory<InventoryDbContext> dbFactory, PackageRepository repository, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    /// <summary>So tief dürfen Pakete ineinander verschachtelt sein; fängt zugleich Zyklen ab.</summary>
    private const int MaxNestingDepth = 5;

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
            .Include(p => p.Steps.OrderBy(s => s.Order)).ThenInclude(s => s.NestedPackage)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
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

    /// <summary>Legt fest, woran ein bereits installiertes Paket erkannt wird; eine leere Erkennung schaltet sie ab.</summary>
    public async Task SaveDetectionAsync(int id, StepConditions? detection, bool verifyAfterInstall, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.Packages.FindAsync([id], ct) ?? throw new KeyNotFoundException(L["Package {0} not found", id]);
        package.Detection = detection is null or { IsEmpty: true } ? null : detection;
        // Ohne Erkennung gäbe es nichts zu prüfen.
        package.VerifyAfterInstall = verifyAfterInstall && package.Detection is not null;
        package.ModifiedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Packages.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        repository.DeleteAll(id);
    }

    /// <summary>Legt einen Schritt an (stepId = null, ans Ende) oder ändert ihn.</summary>
    public async Task SaveStepAsync(int packageId, int? stepId, JobStep definition, int? nestedPackageId, CancellationToken ct = default)
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
        await AppendAsync(db, packageId, prefix: "", manifest, files, included: [], MaxNestingDepth, ct);
        if (manifest.Steps.Count == 0)
            throw new InvalidOperationException(L["The package contains no steps."]);
        // Maßgeblich ist die Erkennung des verteilten Pakets; eingebettete Pakete bringen nur ihre Schritte mit.
        var header = await db.Packages.AsNoTracking().Where(p => p.Id == packageId).Select(p => new { p.Detection, p.VerifyAfterInstall }).FirstAsync(ct);
        manifest.Detection = header.Detection;
        manifest.VerifyAfterInstall = header.VerifyAfterInstall;
        return new BuiltJob(manifest, files);
    }

    private async Task AppendAsync(InventoryDbContext db, int packageId, string prefix, JobManifest manifest, List<JobFile> files, HashSet<int> included, int depth, CancellationToken ct)
    {
        if (depth < 0)
            throw new InvalidOperationException(L["Packages are nested too deeply or in a cycle."]);
        var package = await db.Packages.AsNoTracking().Include(p => p.Steps.OrderBy(s => s.Order)).FirstOrDefaultAsync(p => p.Id == packageId, ct)
                      ?? throw new KeyNotFoundException(L["Package {0} not found", packageId]);

        var folder = $"p{packageId}";
        if (included.Add(packageId))
            files.AddRange(repository.List(packageId).Select(f => new JobFile(repository.Resolve(packageId, f.RelativePath), $@"{folder}\{f.RelativePath}")));

        foreach (var step in package.Steps)
        {
            if (step.NestedPackageId is { } nestedId)
            {
                await AppendAsync(db, nestedId, $"{prefix}{step.Definition.Name} › ", manifest, files, included, depth - 1, ct);
                continue;
            }
            // Kopie, damit das Umschreiben der Pfade nicht auf das geladene Paket durchschlägt.
            var copy = JobJson.Deserialize<JobStep>(JobJson.Serialize(step.Definition))!;
            copy.Name = prefix + copy.Name;
            if (!string.IsNullOrWhiteSpace(copy.File))
                copy.File = $@"{folder}\{copy.File.TrimStart('\\', '/')}";
            manifest.Steps.Add(copy);
        }
    }

    /// <summary>true, wenn <paramref name="packageId"/> (direkt oder über Verschachtelung) <paramref name="searchedId"/> enthält oder selbst ist.</summary>
    private static async Task<bool> ContainsAsync(InventoryDbContext db, int packageId, int searchedId, int depth, CancellationToken ct)
    {
        if (packageId == searchedId || depth < 0)
            return true;
        var nested = await db.PackageSteps.Where(s => s.PackageId == packageId && s.NestedPackageId != null).Select(s => s.NestedPackageId!.Value).ToListAsync(ct);
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
            package.ModifiedUtc = DateTime.UtcNow;
    }
}
