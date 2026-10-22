using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Verwaltet gespeicherte Berichte und führt sie über <see cref="ReportService"/> aus.</summary>
public sealed class SavedReportService(IDbContextFactory<InventoryDbContext> dbFactory, ReportService reports, IStringLocalizer<SharedResource>? localizer = null, SettingsService? settings = null)
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
        if (!Enum.IsDefined(input.Source))
            throw new ArgumentException(L["Unknown report source."]);
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

    public static ReportQuery Query(Report report, TimeZoneInfo? zone = null) =>
        new(report.Source, report.CollectionId, report.Days, report.ColumnKeys, report.SortColumn, report.SortDescending, zone);

    /// <param name="zone">Zeitzone der Zeitangaben (die des Browsers); null = die des Servers.</param>
    public Task<ReportTable> RunAsync(Report report, CancellationToken ct = default, TimeZoneInfo? zone = null) => reports.TableAsync(Query(report, zone), ct);

    public sealed record ReportFile(string FileName, string ContentType, byte[] Content);

    /// <summary>Führt den Bericht aus und bringt ihn in das gewünschte Format.</summary>
    /// <param name="zone">Zeitzone der Zeitangaben; null = die des Servers. <paramref name="localNow"/> sollte in derselben Zone sein.</param>
    public async Task<ReportFile> ExportAsync(Report report, ReportFormat format, DateTimeOffset localNow, CancellationToken ct = default, TimeZoneInfo? zone = null) =>
        Render(report, await RunAsync(report, ct, zone), format, localNow, format == ReportFormat.Pdf ? await BrandingAsync(ct) : null);

    /// <summary>Name und Logo für den Kopf der PDFs; ohne Einstellungsdienst keine.</summary>
    public async Task<ReportBranding?> BrandingAsync(CancellationToken ct = default) =>
        settings is null ? null : await settings.GetReportBrandingAsync(ct);

    /// <summary>Bringt die Zeilen eines Berichts in ein Dateiformat. Download und Versand gehen beide hier durch.</summary>
    public ReportFile Render(Report report, ReportTable table, ReportFormat format, DateTimeOffset localNow, ReportBranding? branding = null)
    {
        var name = FileName(report);
        return format switch
        {
            ReportFormat.Pdf => new ReportFile(name + ".pdf", ReportPdf.ContentType, ReportPdf.Write(table, new ReportPdfOptions(
                report.Name, Subtitle(report, table.Rows.Count, localNow), report.Description, branding?.Organization, branding?.Logo,
                L["Page {0} of {1}"], L["First {0} of {1} rows."], L["No entries"], L["Columns {0}/{1}"], L["Created with Inventory"]))),
            ReportFormat.Csv => new ReportFile(name + ".csv", "text/csv; charset=utf-8", ReportExport.CsvBytes(table)),
            _ => new ReportFile(name + ".xlsx", ReportExport.XlsxContentType, ReportExport.Xlsx(table, report.Name)),
        };
    }

    /// <summary>Die Zeile unter dem Titel eines gedruckten Berichts: Quelle, Sammlung, Zeitraum, Zeilenzahl und Stand. Die Beschreibung steht darunter.</summary>
    public string Subtitle(Report report, int rows, DateTimeOffset localNow)
    {
        var parts = new List<string> { SourceName(report.Source) };
        if (report.Collection is not null)
            parts.Add(report.Collection.Name);
        if (Report.HasPeriod(report.Source))
            parts.Add(L["last {0} days", report.Days ?? ReportService.DefaultDays]);
        parts.Add(L["{0} rows", rows]);
        parts.Add(L["as of {0}", localNow.ToString("g")]);
        return string.Join("  ·  ", parts);
    }

    public string SourceName(ReportSource source) => source switch
    {
        ReportSource.Computers => L["Computers"],
        ReportSource.Software => L["Installed software"],
        ReportSource.PendingUpdates => L["Pending updates"],
        ReportSource.Deployments => L["Deployments"],
        ReportSource.Changes => L["Changes"],
        _ => source.ToString(),
    };

    /// <summary>Dateiname ohne Endung: der Name des Berichts, auf das reduziert, was jedes Dateisystem annimmt.</summary>
    public static string FileName(Report report)
    {
        var name = new string(report.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_').ToArray()).Trim();
        return name.Length == 0 ? "report" : name.Length > 80 ? name[..80] : name;
    }
}
