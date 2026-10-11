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
        var dynamic = collections.Where(c => c.IsDynamic).ToList();
        var computers = dynamic.Count > 0 ? await LoadForRulesAsync(db, dynamic, ct) : [];
        var context = new RuleContext(collections);
        return collections
            .Select(c => new CollectionSummary(c, c.IsDynamic ? computers.Count(x => CollectionEvaluator.Matches(c, x, context)) : c.Members.Count))
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
        // Verweist die Sammlung auf andere, werden diese mit ausgewertet.
        var all = RuleContext.References(collection).Any()
            ? await db.Collections.AsNoTracking().Include(c => c.Members).ToListAsync(ct)
            : [collection];
        var context = new RuleContext(all);
        // Geladen wird nur, was diese Sammlung und die von ihr genannten auswerten, nicht was irgendeine andere braucht.
        return (await LoadForRulesAsync(db, context.Closure(collection).Where(c => c.IsDynamic), ct)).Where(c => CollectionEvaluator.Matches(collection, c, context)).Select(c => c.Id).ToHashSet();
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
        await CheckReferencesAsync(db, collection, ct);
        await db.SaveChangesAsync(ct);
        return collection;
    }

    /// <summary>Verweise auf andere Sammlungen müssen existieren, dürfen nicht im Kreis führen und nicht tiefer als <see cref="RuleContext.MaxDepth"/> reichen.</summary>
    private async Task CheckReferencesAsync(InventoryDbContext db, Collection collection, CancellationToken ct)
    {
        if (!RuleContext.References(collection).Any() && !collection.Rules.Any(r => r.Field == RuleField.InCollection))
            return;
        var others = await db.Collections.AsNoTracking().Where(c => c.Id != collection.Id).ToListAsync(ct);
        foreach (var rule in collection.Rules.Where(r => r.Field == RuleField.InCollection))
        {
            if (!int.TryParse(rule.Value.Trim(), out var target) || (target != collection.Id && others.All(c => c.Id != target)))
                throw new InvalidOperationException(L["The collection referenced by a rule does not exist (anymore)."]);
            if (target == collection.Id)
                throw new InvalidOperationException(L["A collection cannot refer to itself."]);
        }
        // Eine neue Sammlung hat noch keine Kennung und kann deshalb von keiner anderen genannt werden.
        var depth = new RuleContext([.. others, collection]).Depth(collection);
        if (depth < 0)
            throw new InvalidOperationException(L["These rules would make collections refer to each other in a circle."]);
        // Auch wer auf diese Sammlung verweist, wird dadurch tiefer.
        var deepest = collection.Id == 0 ? depth : MaxDepth(new RuleContext([.. others, collection]), others.Append(collection));
        if (deepest > RuleContext.MaxDepth)
            throw new InvalidOperationException(L["Collections may refer to each other at most {0} levels deep.", RuleContext.MaxDepth]);
    }

    /// <exception cref="InvalidOperationException">Eine andere Sammlung verweist in ihren Regeln auf diese.</exception>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var referencing = (await db.Collections.AsNoTracking().Where(c => c.Id != id && c.IsDynamic).ToListAsync(ct))
            .Where(c => RuleContext.References(c).Contains(id)).Select(c => c.Name).ToList();
        if (referencing.Count > 0)
            throw new InvalidOperationException(L["This collection is used in the rules of: {0}. Change those rules first.", string.Join(", ", referencing)]);
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

    /// <summary>Lädt die Rechner nur mit den Listen, die die Regeln dieser Sammlungen tatsächlich auswerten.</summary>
    private static Task<List<Computer>> LoadForRulesAsync(InventoryDbContext db, IEnumerable<Collection> collections, CancellationToken ct)
    {
        var needed = collections.SelectMany(c => c.Rules).SelectMany(r => CollectionEvaluator.RequiredData(r.Field)).ToHashSet();
        var query = db.Computers.AsNoTracking().AsQueryable();
        foreach (var navigation in needed)
            query = query.Include(navigation);
        return (needed.Count > 1 ? query.AsSplitQuery() : query).ToListAsync(ct);
    }

    private static int MaxDepth(RuleContext context, IEnumerable<Collection> collections) => collections.Max(context.Depth);
}
