using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="Programs">Die gefundenen Programmnamen, auf die das Muster passt – zum Prüfen, ob es zu weit oder zu eng gefasst ist.</param>
public sealed record LicenseUsage(SoftwareLicense License, int Installed, List<string> Computers, List<string> Programs)
{
    /// <summary>Positiv = so viele Lizenzen fehlen; negativ = so viele sind frei.</summary>
    public int Missing => Installed - License.Owned;
    public bool Exceeded => Missing > 0;
}

/// <summary>Verwaltet die Lizenzen und zählt, auf wie vielen Rechnern die zugehörige Software installiert ist.</summary>
public sealed class LicenseService(IDbContextFactory<InventoryDbContext> dbFactory, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    /// <summary>Alle Lizenzen mit ihrem Verbrauch, nach Name geordnet.</summary>
    public async Task<List<LicenseUsage>> UsageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var licenses = await db.SoftwareLicenses.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        if (licenses.Count == 0)
            return [];
        var installs = await db.Software.AsNoTracking().Select(s => new { s.ComputerId, s.Name }).Distinct().ToListAsync(ct);
        var names = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        // Jeden vorkommenden Programmnamen nur einmal gegen jedes Muster prüfen.
        var byProgram = installs.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(i => i.ComputerId).ToList(), StringComparer.OrdinalIgnoreCase);
        return licenses.Select(license =>
        {
            var programs = byProgram.Keys.Where(program => SoftwareLicense.Matches(license.Pattern, program)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            // Mehrere passende Programme (oder Versionen) auf einem Rechner verbrauchen eine Lizenz.
            var computers = programs.SelectMany(program => byProgram[program]).Distinct().Select(id => names.GetValueOrDefault(id, "?")).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return new LicenseUsage(license, computers.Count, computers, programs);
        }).ToList();
    }

    /// <summary>Legt eine Lizenz an (Id 0) oder ändert sie.</summary>
    /// <exception cref="ArgumentException">Name oder Muster fehlen.</exception>
    public async Task<SoftwareLicense> SaveAsync(SoftwareLicense input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException(L["Name is missing"]);
        if (string.IsNullOrWhiteSpace(input.Pattern) || input.Pattern.Trim().Trim('*').Length == 0)
            throw new ArgumentException(L["Please enter the program name to count."]);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var license = input.Id == 0 ? null : await db.SoftwareLicenses.FindAsync([input.Id], ct) ?? throw new KeyNotFoundException(L["Licence {0} not found", input.Id]);
        if (license is null)
        {
            license = new SoftwareLicense { Name = input.Name.Trim(), Pattern = input.Pattern.Trim() };
            db.SoftwareLicenses.Add(license);
        }
        license.Name = input.Name.Trim();
        license.Pattern = input.Pattern.Trim();
        license.Owned = Math.Max(input.Owned, 0);
        license.CostPerLicense = input.CostPerLicense is >= 0 ? input.CostPerLicense : null;
        license.ValidUntil = input.ValidUntil;
        license.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        await db.SaveChangesAsync(ct);
        return license;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SoftwareLicenses.Where(l => l.Id == id).ExecuteDeleteAsync(ct);
    }
}
