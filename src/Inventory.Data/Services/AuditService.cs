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

    /// <summary>Kennung des jüngsten Eintrags; ändert sie sich, gibt es Neues anzuzeigen.</summary>
    public async Task<long> LatestIdAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries.MaxAsync(e => (long?)e.Id, ct) ?? 0;
    }

    public async Task<List<AuditEntry>> ListAsync(int take = 500, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries.AsNoTracking().OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);
    }
}
