using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Verwaltet gespeicherte Berichte und führt sie über <see cref="ReportService"/> aus.</summary>
public sealed class SavedReportService(IDbContextFactory<InventoryDbContext> dbFactory, ReportService reports, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    public async Task<List<Report>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Reports.AsNoTracking().Include(r => r.Collection).OrderBy(r => r.Name).ToListAsync(ct);
    }

    public async Task<Report?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Reports.AsNoTracking().Include(r => r.Collection).FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>Legt einen Bericht an (Id 0) oder ändert ihn.</summary>
    /// <exception cref="ArgumentException">Der Name fehlt oder eine Spalte gehört nicht zur Quelle.</exception>
    public async Task<Report> SaveAsync(Report input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException(L["Name is missing"]);
        var known = reports.ColumnsOf(input.Source).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (input.ColumnKeys.FirstOrDefault(key => !known.Contains(key)) is { } unknown)
            throw new ArgumentException(L["Unknown column \"{0}\".", unknown]);
        if (!string.IsNullOrEmpty(input.SortColumn) && !known.Contains(input.SortColumn))
            throw new ArgumentException(L["Unknown column \"{0}\".", input.SortColumn]);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var report = input.Id == 0 ? null : await db.Reports.FindAsync([input.Id], ct) ?? throw new KeyNotFoundException(L["Report {0} not found", input.Id]);
        if (report is null)
        {
            report = new Report { Name = input.Name.Trim() };
            db.Reports.Add(report);
        }
        report.Name = input.Name.Trim();
        report.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        report.Source = input.Source;
        report.CollectionId = Report.HasCollectionFilter(input.Source) ? input.CollectionId : null;
        report.Days = Report.HasPeriod(input.Source) && input.Days is > 0 ? Math.Min(input.Days.Value, 3650) : null;
        report.Columns = input.ColumnKeys.Count == 0 ? null : string.Join(',', input.ColumnKeys);
        report.SortColumn = string.IsNullOrEmpty(input.SortColumn) ? null : input.SortColumn;
        report.SortDescending = input.SortDescending;
        await db.SaveChangesAsync(ct);
        return report;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Reports.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public static ReportQuery Query(Report report) =>
        new(report.Source, report.CollectionId, report.Days, report.ColumnKeys, report.SortColumn, report.SortDescending);

    public Task<ReportTable> RunAsync(Report report, CancellationToken ct = default) => reports.TableAsync(Query(report), ct);

    /// <summary>Dateiname ohne Endung: der Name des Berichts, auf das reduziert, was jedes Dateisystem annimmt.</summary>
    public static string FileName(Report report)
    {
        var name = new string(report.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_').ToArray()).Trim();
        return name.Length == 0 ? "report" : name.Length > 80 ? name[..80] : name;
    }
}
