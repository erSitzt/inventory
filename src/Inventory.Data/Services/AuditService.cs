using System.Text;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Eingrenzung der Protokollansicht; leere Angaben schränken nicht ein.</summary>
public sealed record AuditFilter(string? User = null, string? Action = null, DateTime? FromUtc = null, DateTime? ToUtc = null, string? Search = null);

public sealed class AuditService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    private const int MaxDetailLength = 2000;
    /// <summary>Obergrenze für einen Export; darüber hinaus hilft ein engerer Zeitraum.</summary>
    public const int MaxExportRows = 200_000;

    public async Task LogAsync(AuditActor actor, string action, string? details = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditEntries.Add(new AuditEntry
        {
            User = actor.User,
            Role = actor.Role is null or AccessRole.None ? null : actor.Role,
            Source = actor.Source,
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

    public async Task<List<AuditEntry>> ListAsync(int take = 500, CancellationToken ct = default, AuditFilter? filter = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Query(db, filter).OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);
    }

    /// <summary>Benutzer und Aktionen, die im Protokoll vorkommen – für die Auswahllisten der Filter.</summary>
    public async Task<(List<string> Users, List<string> Actions)> FilterValuesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.AuditEntries.Select(e => e.User).Distinct().OrderBy(u => u).ToListAsync(ct);
        var actions = await db.AuditEntries.Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
        return (users, actions);
    }

    /// <summary>Protokoll als CSV, jüngste Einträge zuerst; Zeiten in der Ortszeit des Servers.</summary>
    public async Task<string> CsvAsync(IStringLocalizer L, AuditFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var csv = new StringBuilder();
        ReportExport.Row(csv, L["Time"], L["User"], L["Role"], L["Source"], L["Action"], L["Details"]);
        await foreach (var e in Query(db, filter).OrderByDescending(e => e.Id).Take(MaxExportRows).AsAsyncEnumerable().WithCancellation(ct))
            ReportExport.Row(csv, DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                e.User, e.Role?.ToString(), e.Source == AuditSource.Unknown ? null : e.Source.ToString(), e.Action, e.Details);
        return csv.ToString();
    }

    /// <summary>Entfernt Einträge, die älter als die Aufbewahrungsfrist sind; 0 Tage = nichts löschen.</summary>
    public async Task<int> CleanupAsync(int days, CancellationToken ct = default)
    {
        if (days <= 0)
            return 0;
        var limit = DateTime.UtcNow.AddDays(-days);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries.Where(e => e.TimeUtc < limit).ExecuteDeleteAsync(ct);
    }

    private static IQueryable<AuditEntry> Query(InventoryDbContext db, AuditFilter? filter)
    {
        var query = db.AuditEntries.AsNoTracking();
        if (filter is null)
            return query;
        if (!string.IsNullOrWhiteSpace(filter.User))
            query = query.Where(e => e.User == filter.User);
        if (!string.IsNullOrWhiteSpace(filter.Action))
            query = query.Where(e => e.Action == filter.Action);
        if (filter.FromUtc is { } from)
            query = query.Where(e => e.TimeUtc >= from);
        if (filter.ToUtc is { } to)
            query = query.Where(e => e.TimeUtc < to);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Platzhalter des Benutzers wörtlich nehmen.
            var pattern = "%" + filter.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(e => EF.Functions.Like(e.User, pattern, "\\") || EF.Functions.Like(e.Action, pattern, "\\") || (e.Details != null && EF.Functions.Like(e.Details, pattern, "\\")));
        }
        return query;
    }
}
