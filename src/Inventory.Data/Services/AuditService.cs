using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed class AuditService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    private const int MaxDetailLength = 2000;

    public async Task LogAsync(string user, string action, string? details = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditEntries.Add(new AuditEntry
        {
            User = user,
            Action = action,
            Details = details is { Length: > MaxDetailLength } ? details[..MaxDetailLength] + " …" : details,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AuditEntry>> ListAsync(int take = 500, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries.AsNoTracking().OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);
    }
}
