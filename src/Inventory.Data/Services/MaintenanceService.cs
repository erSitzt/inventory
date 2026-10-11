using System.Text.Json;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>
/// Wartungsfenster: für einen Rechner gelten die Fenster aller Sammlungen, in denen er Mitglied ist (er darf laufen, sobald eines
/// offen ist). Gehört er zu keiner Sammlung mit Fenster, gilt das Standardfenster; ohne Standardfenster gibt es keine Einschränkung.
/// </summary>
public sealed class MaintenanceService(IDbContextFactory<InventoryDbContext> dbFactory, CollectionService collections, SettingsService settings, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<MaintenanceWindow?> GetDefaultAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.MaintenanceDefaultWindow, ct) is { Length: > 0 } json ? JsonSerializer.Deserialize<MaintenanceWindow>(json) : null;

    public Task SetDefaultAsync(MaintenanceWindow? window, CancellationToken ct = default) =>
        settings.SetAsync(SettingKeys.MaintenanceDefaultWindow, Normalize(window) is { } valid ? JsonSerializer.Serialize(valid) : null, ct);

    public async Task SetAsync(int collectionId, MaintenanceWindow? window, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var collection = await db.Collections.FindAsync([collectionId], ct) ?? throw new KeyNotFoundException($"Collection {collectionId} not found");
        collection.MaintenanceWindow = Normalize(window);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ein Fenster ohne Wochentag wäre nie offen und gilt deshalb als "kein Fenster".</summary>
    public static MaintenanceWindow? Normalize(MaintenanceWindow? window) => window is null || (window.DaysOfWeek & 0x7F) == 0 ? null : window;

    /// <summary>
    /// Rechner, deren Wartungsfenster gerade geschlossen ist, mit dem Zeitpunkt (Ortszeit), zu dem das nächste beginnt.
    /// Rechner ohne Einschränkung oder mit offenem Fenster fehlen im Ergebnis.
    /// </summary>
    public async Task<Dictionary<int, DateTime?>> ClosedAsync(IReadOnlyCollection<int> computerIds, CancellationToken ct = default)
    {
        var now = _time.GetLocalNow().DateTime;
        List<Collection> withWindow;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            withWindow = (await db.Collections.AsNoTracking().ToListAsync(ct)).Where(c => c.MaintenanceWindow is not null).ToList();

        var windows = computerIds.Distinct().ToDictionary(id => id, _ => new List<MaintenanceWindow>());
        foreach (var collection in withWindow)
        {
            foreach (var id in await collections.GetMemberIdsAsync(collection.Id, ct))
            {
                if (windows.TryGetValue(id, out var list))
                    list.Add(collection.MaintenanceWindow!);
            }
        }

        var fallback = await GetDefaultAsync(ct);
        var closed = new Dictionary<int, DateTime?>();
        foreach (var (id, own) in windows)
        {
            var effective = own.Count > 0 ? own : fallback is null ? [] : [fallback];
            if (effective.Count > 0 && !effective.Any(w => w.Contains(now)))
                closed[id] = effective.Select(w => w.NextStart(now)).Where(start => start is not null).Min();
        }
        return closed;
    }
}
