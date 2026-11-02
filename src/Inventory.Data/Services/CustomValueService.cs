using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Ergebnis eines Inventarskripts für einen Rechner.</summary>
/// <param name="Stored">Felder, deren Wert gesetzt (oder entfernt) wurde.</param>
public sealed record CustomScriptOutcome(string Script, int Stored, IReadOnlyList<string> Warnings);

/// <summary>
/// Werte eigener Inventarfelder: speichert sie normalisiert, schreibt Änderungen in den Verlauf des Rechners und übernimmt
/// die Ausgabe der Inventarskripte. Nur Felder, die ein geladenes Plugin angemeldet hat, werden angenommen.
/// </summary>
public sealed class CustomValueService(IDbContextFactory<InventoryDbContext> dbFactory, ICustomFieldCatalog? fields = null, TimeProvider? clock = null)
{
    private readonly ICustomFieldCatalog _fields = fields ?? EmptyCustomFieldCatalog.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<CustomFieldInfo> Fields => _fields.Fields;

    public IReadOnlyList<CustomScriptInfo> Scripts => _fields.Scripts;

    /// <summary>Setzt einen Wert; null oder leer entfernt ihn.</summary>
    /// <exception cref="ArgumentException">Unbekanntes Feld oder ein Wert, der nicht zum Typ passt.</exception>
    public async Task SetAsync(int computerId, string key, string? value, CancellationToken ct = default) =>
        await SetManyAsync(computerId, new Dictionary<string, string?> { [key] = value }, strict: true, ct);

    /// <summary>Setzt mehrere Werte in einem Zug; liefert Warnungen zu Feldern, die nicht übernommen wurden.</summary>
    /// <param name="strict">true = beim ersten Fehler abbrechen (ArgumentException), statt ihn als Warnung zu melden.</param>
    public async Task<(int Stored, List<string> Warnings)> SetManyAsync(int computerId, IReadOnlyDictionary<string, string?> values, bool strict = false, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var normalized = new Dictionary<CustomFieldInfo, string?>();
        foreach (var (key, raw) in values)
        {
            if (_fields.Find(key) is not { } field)
            {
                if (strict)
                    throw new ArgumentException($"Unknown custom field '{key}'.");
                warnings.Add($"Unknown field '{key}'.");
                continue;
            }
            try
            {
                normalized[field] = string.IsNullOrWhiteSpace(raw) ? null : CustomValues.Normalize(field.Kind, raw);
            }
            catch (FormatException ex)
            {
                if (strict)
                    throw new ArgumentException($"{field.Title}: {ex.Message}", ex);
                warnings.Add($"{field.Title}: {ex.Message}");
            }
        }
        if (normalized.Count == 0)
            return (0, warnings);

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Computers.AnyAsync(c => c.Id == computerId, ct))
            throw new KeyNotFoundException($"Computer {computerId} not found.");
        var keys = normalized.Keys.Select(f => f.Key).ToList();
        var existing = await db.CustomValues.Where(v => v.ComputerId == computerId && keys.Contains(v.Key)).ToDictionaryAsync(v => v.Key, StringComparer.OrdinalIgnoreCase, ct);
        var stored = 0;
        foreach (var (field, value) in normalized)
        {
            var old = existing.GetValueOrDefault(field.Key);
            if (old?.Value == value)
            {
                if (old is not null)
                    old.UpdatedUtc = now;
                continue;
            }
            // Wie beim Scan: jede Änderung steht im Verlauf des Rechners, unter dem Namen des Plugins.
            db.InventoryChanges.Add(new InventoryChange
            {
                ComputerId = computerId, TimeUtc = now, Category = field.Source, Item = field.Title,
                Kind = old is null ? ChangeKind.Added : value is null ? ChangeKind.Removed : ChangeKind.Changed,
                OldValue = old?.Value, NewValue = value,
            });
            if (value is null)
                db.CustomValues.Remove(old!);
            else if (old is null)
                db.CustomValues.Add(new ComputerCustomValue { ComputerId = computerId, Key = field.Key, Value = value, UpdatedUtc = now });
            else
                (old.Value, old.UpdatedUtc) = (value, now);
            stored++;
        }
        await db.SaveChangesAsync(ct);
        return (stored, warnings);
    }

    public async Task<List<ComputerCustomValue>> GetAsync(int computerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CustomValues.AsNoTracking().Where(v => v.ComputerId == computerId).ToListAsync(ct);
    }

    /// <summary>Alle Werte aller Rechner, für Listen und Berichte: Rechner → Schlüssel → Wert.</summary>
    public async Task<Dictionary<int, Dictionary<string, string>>> AllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.CustomValues.AsNoTracking().ToListAsync(ct))
            .GroupBy(v => v.ComputerId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase));
    }

    public async Task<List<ComputerCustomScriptRun>> RunsAsync(int computerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CustomScriptRuns.AsNoTracking().Where(r => r.ComputerId == computerId).OrderBy(r => r.Script).ToListAsync(ct);
    }

    /// <summary>
    /// Übernimmt die Ausgabe eines Inventarskripts. Eigenschaften dürfen den vollen Schlüssel oder nur den Namen ohne
    /// "&lt;Plugin-Id&gt;." nennen. Ist die Ausgabe unbrauchbar, bleiben die bisherigen Werte stehen.
    /// </summary>
    /// <param name="error">Fehler beim Ausführen (Schritt fehlgeschlagen); dann wird nur der Lauf vermerkt.</param>
    public async Task<CustomScriptOutcome> ApplyScriptAsync(int computerId, CustomScriptInfo script, string? output, string? error, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var stored = 0;
        if (error is not null)
        {
            warnings.Add(error);
        }
        else
        {
            try
            {
                var values = CustomValues.ParseOutput(output).ToDictionary(
                    p => _fields.Find(p.Key) is not null ? p.Key : script.KeyPrefix + p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                (stored, var problems) = await SetManyAsync(computerId, values, strict: false, ct);
                warnings.AddRange(problems);
            }
            catch (FormatException ex)
            {
                warnings.Add(ex.Message);
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.CustomScriptRuns.FirstOrDefaultAsync(r => r.ComputerId == computerId && r.Script == script.Id, ct);
        if (run is null)
            db.CustomScriptRuns.Add(run = new ComputerCustomScriptRun { ComputerId = computerId, Script = script.Id });
        run.RunUtc = _clock.GetUtcNow().UtcDateTime;
        run.Error = warnings.Count == 0 ? null : string.Join(" ", warnings);
        await db.SaveChangesAsync(ct);
        return new CustomScriptOutcome(script.Id, stored, warnings);
    }
}
