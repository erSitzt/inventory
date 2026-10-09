using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

public sealed record CollectionSummary(Collection Collection, int Count);

public sealed class CollectionService(IDbContextFactory<InventoryDbContext> dbFactory, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    public async Task<List<CollectionSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var collections = await db.Collections.AsNoTracking().Include(c => c.Members).OrderBy(c => c.Name).ToListAsync(ct);
        // Dynamische Sammlungen werden im Speicher ausgewertet; bei einigen hundert Rechnern ist das schneller als jede SQL-Übersetzung der Regeln.
        var computers = collections.Any(c => c.IsDynamic) ? await LoadForRulesAsync(db, ct) : [];
        return collections
            .Select(c => new CollectionSummary(c, c.IsDynamic ? computers.Count(x => CollectionEvaluator.Matches(c, x)) : c.Members.Count))
            .ToList();
    }

    public async Task<Collection?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>IDs der Rechner, die zur Sammlung gehören (fest zugeordnet bzw. aktuell zu den Regeln passend).</summary>
    public async Task<HashSet<int>> GetMemberIdsAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var collection = await db.Collections.AsNoTracking().Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null)
            return [];
        if (!collection.IsDynamic)
            return collection.Members.Select(m => m.Id).ToHashSet();
        return (await LoadForRulesAsync(db, ct)).Where(c => CollectionEvaluator.Matches(collection, c)).Select(c => c.Id).ToHashSet();
    }

    public async Task<Collection> SaveAsync(int? id, string name, string? description, bool isDynamic, bool matchAll, List<CollectionRule> rules, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Collection collection;
        if (id is null)
        {
            collection = new Collection { Name = name.Trim(), IsDynamic = isDynamic };
            db.Collections.Add(collection);
        }
        else
        {
            collection = await db.Collections.FindAsync([id.Value], ct) ?? throw new KeyNotFoundException(L["Collection {0} not found", id]);
            collection.Name = name.Trim();
        }
        collection.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        collection.MatchAll = matchAll;
        collection.Rules = collection.IsDynamic ? rules.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList() : [];
        await db.SaveChangesAsync(ct);
        return collection;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Collections.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<int> AddMembersAsync(int collectionId, IEnumerable<int> computerIds, CancellationToken ct = default)
    {
        var ids = computerIds.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var collection = await db.Collections.Include(c => c.Members).FirstAsync(c => c.Id == collectionId, ct);
        var existing = collection.Members.Select(m => m.Id).ToHashSet();
        var added = await db.Computers.Where(c => ids.Contains(c.Id) && !existing.Contains(c.Id)).ToListAsync(ct);
        collection.Members.AddRange(added);
        await db.SaveChangesAsync(ct);
        return added.Count;
    }

    public async Task<int> RemoveMembersAsync(int collectionId, IEnumerable<int> computerIds, CancellationToken ct = default)
    {
        var ids = computerIds.ToHashSet();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var collection = await db.Collections.Include(c => c.Members).FirstAsync(c => c.Id == collectionId, ct);
        var removed = collection.Members.RemoveAll(m => ids.Contains(m.Id));
        await db.SaveChangesAsync(ct);
        return removed;
    }

    private static Task<List<Computer>> LoadForRulesAsync(InventoryDbContext db, CancellationToken ct) =>
        db.Computers.AsNoTracking().Include(c => c.Software).Include(c => c.LocalAdmins).AsSplitQuery().ToListAsync(ct);
}
