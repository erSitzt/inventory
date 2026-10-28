using Inventory.Core;
using System.Text;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Api;

public sealed record AuditDto(long Id, DateTime TimeUtc, string User, AccessRole? Role, AuditSource Source, string Action, string? Details);

public sealed record SavedReportDto(int Id, string Name, string? Description, ReportSource Source, int? CollectionId, int? Days, IReadOnlyList<string> Columns, string? SortColumn, bool SortDescending);

public sealed record SavedReportRequest(string Name, ReportSource Source, string? Description = null, int? CollectionId = null, int? Days = null, IReadOnlyList<string>? Columns = null,
    string? SortColumn = null, bool SortDescending = false);

public sealed record LicenseRequest(string Name, string Pattern, int Owned, decimal? CostPerLicense = null, DateOnly? ValidUntil = null, string? Notes = null);

public sealed record LicenseDto(int Id, string Name, string Pattern, int Owned, int Installed, int Missing, decimal? CostPerLicense, DateOnly? ValidUntil, string? Notes,
    IReadOnlyList<string> Computers, IReadOnlyList<string> Programs);

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // Aus der Oberfläche heruntergeladen (Cookie): Zeiten in der Zeitzone des Browsers, sonst in der des Servers.
        app.MapGet("/api/reports/computers.csv", async (ReportService reports, HttpContext http, CancellationToken ct) =>
            Csv(await reports.ComputersCsvAsync(ct, Zone(http)), "rechner.csv"))
            .WithTags("Reports").WithSummary("Computers as CSV").WithDescription("All computers with inventory and security status; UTF-8 with BOM, opens directly in Excel.").Produces<string>(200, "text/csv");

        app.MapGet("/api/reports/software.csv", async (ReportService reports, HttpContext http, CancellationToken ct) =>
            Csv(await reports.SoftwareCsvAsync(ct, Zone(http)), "software.csv"))
            .WithTags("Reports").WithSummary("Software as CSV").WithDescription("One row per computer and installed program.").Produces<string>(200, "text/csv");

        app.MapGet("/api/reports/software", async (ReportService reports, CancellationToken ct) => await reports.SoftwareAsync(ct))
            .WithTags("Reports").WithSummary("Software overview").WithDescription("Every combination of program and version with the number of computers it is installed on.");

        app.MapGet("/api/reports/updates.csv", async (ReportService reports, HttpContext http, CancellationToken ct) =>
            Csv(await reports.PendingUpdatesCsvAsync(ct, Zone(http)), "updates.csv"))
            .WithTags("Reports").WithSummary("Pending updates as CSV").WithDescription("One row per computer and pending update, as of each computer's last own update search.").Produces<string>(200, "text/csv");

        app.MapGet("/api/reports/updates", async (ReportService reports, CancellationToken ct) => (await reports.PatchComplianceAsync(ct)).Updates)
            .WithTags("Reports").WithSummary("Pending updates overview").WithDescription("Every pending update with the number of computers that miss it. key = KB numbers, otherwise the title; isSecurity = security or critical update, or one with an MSRC severity.");

        app.MapGet("/api/reports/updates/computers", async (string key, ReportService reports, CancellationToken ct) =>
            (await reports.ComputersMissingUpdateAsync(key, ct)).Select(c => new { c.Id, c.Name, c.UpdateLastSearchUtc, c.UpdateLastInstallUtc }))
            .WithTags("Reports").WithSummary("Computers missing an update").WithDescription("key as returned by the pending updates overview.");

        var saved = app.MapGroup("/api/reports/saved").WithTags("Reports").AddEndpointFilter(MapErrors).WritesRequire(AccessControl.AdminPolicy);

        saved.MapPost("", async (SavedReportRequest request, SavedReportService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var report = await service.SaveAsync(ToReport(0, request), ct);
            await audit.LogAsync(http.Actor(), "Report created via API", report.Name, ct);
            return Results.Created($"/api/reports/saved/{report.Id}", ToDto(report));
        }).WithSummary("Create a saved report").WithDescription("source: Computers, Software, PendingUpdates, Deployments or Changes. columns: keys as returned by running a report, empty = all. days only for Deployments and Changes.");

        saved.MapPut("{id:int}", async (int id, SavedReportRequest request, SavedReportService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var report = await service.SaveAsync(ToReport(id, request), ct);
            await audit.LogAsync(http.Actor(), "Report changed via API", report.Name, ct);
            return Results.Ok(ToDto(report));
        }).WithSummary("Change a saved report");

        saved.MapDelete("{id:int}", async (int id, SavedReportService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is not { } report)
                return Results.NotFound();
            await service.DeleteAsync(id, ct);
            await audit.LogAsync(http.Actor(), "Report deleted via API", report.Name, ct);
            return Results.NoContent();
        }).WithSummary("Delete a saved report");

        saved.MapGet("", async (SavedReportService service, CancellationToken ct) =>
            (await service.ListAsync(ct)).Select(ToDto))
            .WithSummary("Saved reports").WithDescription("The reports set up under Reports. columns is empty when the report shows all columns of its source.");

        saved.MapGet("{id:int}", async (int id, SavedReportService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } report && await service.RunAsync(report, ct) is var table
                ? Results.Ok(new { columns = table.Columns, rows = table.Rows })
                : Results.NotFound())
            .WithSummary("Run a saved report").WithDescription("columns with key, title and numeric; rows as arrays of text in the order of the columns.");

        foreach (var (extension, format, summary) in new[] { ("csv", ReportFormat.Csv, "Saved report as CSV"), ("xlsx", ReportFormat.Excel, "Saved report as Excel workbook"), ("pdf", ReportFormat.Pdf, "Saved report as PDF") })
        {
            saved.MapGet($"{{id:int}}.{extension}", async (int id, SavedReportService service, TimeProvider time, HttpContext http, CancellationToken ct) =>
            {
                if (await service.GetAsync(id, ct) is not { } report)
                    return Results.NotFound();
                // Aus der Oberfläche heruntergeladen: Zeiten in der Zeitzone des Browsers; per API ohne Cookie in der des Servers.
                var zone = Zone(http) ?? time.LocalTimeZone;
                var file = await service.ExportAsync(report, format, TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone), ct, zone);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }).WithSummary(summary).WithDescription(format switch
            {
                ReportFormat.Excel => "One sheet with a fixed, filterable header row; numeric columns are stored as numbers.",
                ReportFormat.Pdf => $"A4 landscape with organization name and logo from the settings, the header row on every page and wrapped long values; tables too wide for one page continue on further pages with the first column repeated. At most {ReportPdf.MaxRows} rows.",
                _ => "Semicolon-separated, UTF-8 with BOM.",
            });
        }

        var licenses = app.MapGroup("/api/licenses").WithTags("Licences").AddEndpointFilter(MapErrors).WritesRequire(AccessControl.AdminPolicy);

        licenses.MapGet("", async (LicenseService service, CancellationToken ct) => (await service.UsageAsync(ct)).Select(ToDto))
            .WithSummary("Licences with their usage").WithDescription("installed = computers with at least one matching program; missing > 0 means more installations than licences. programs lists the installed program names the pattern matches.");

        licenses.MapPost("", async (LicenseRequest request, LicenseService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var license = await service.SaveAsync(ToLicense(0, request), ct);
            await audit.LogAsync(http.Actor(), "Licence created via API", license.Name, ct);
            return Results.Created($"/api/licenses/{license.Id}", ToDto((await service.UsageAsync(ct)).First(u => u.License.Id == license.Id)));
        }).WithSummary("Record a licence").WithDescription("pattern: text the program name must contain; with * the whole name must match.");

        licenses.MapPut("{id:int}", async (int id, LicenseRequest request, LicenseService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var license = await service.SaveAsync(ToLicense(id, request), ct);
            await audit.LogAsync(http.Actor(), "Licence changed via API", license.Name, ct);
            return Results.Ok(ToDto((await service.UsageAsync(ct)).First(u => u.License.Id == license.Id)));
        }).WithSummary("Change a licence");

        licenses.MapDelete("{id:int}", async (int id, LicenseService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            await audit.LogAsync(http.Actor(), "Licence deleted via API", $"Licence {id}", ct);
            return Results.NoContent();
        }).WithSummary("Delete a licence");

        var audit = app.MapGroup("/api/audit").WithTags("Audit log").RequireOperator();

        audit.MapGet("", async (AuditService service, string? user, string? action, DateTime? from, DateTime? to, string? q, int? take, CancellationToken ct) =>
            (await service.ListAsync(Math.Clamp(take ?? 500, 1, 5000), ct, Filter(user, action, from, to, q)))
                .Select(e => new AuditDto(e.Id, DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc), e.User, e.Role, e.Source, e.Action, e.Details)))
            .WithSummary("Audit log entries").WithDescription("Newest first. Optional filters: user and action (exact), from and to (to is exclusive; values without a time zone count as server local time), q (text in user, action or details), take (default 500, at most 5000).");

        audit.MapGet("export.csv", async (AuditService service, HttpContext http, IStringLocalizer<SharedResource> L, string? user, string? action, DateTime? from, DateTime? to, string? q, CancellationToken ct) =>
        {
            var content = await service.CsvAsync(L, Filter(user, action, from, to, q), ct, Zone(http));
            await service.LogAsync(http.Actor(), L["Audit log exported"], http.Request.QueryString.HasValue ? Uri.UnescapeDataString(http.Request.QueryString.Value!.TrimStart('?')) : null, ct);
            return Csv(content, "audit.csv");
        }).WithSummary("Audit log as CSV").WithDescription($"Same filters as the list; at most {AuditService.MaxExportRows} rows, newest first. The export itself is logged.").Produces<string>(200, "text/csv");

        return app;
    }

    private static SoftwareLicense ToLicense(int id, LicenseRequest r) => new()
    {
        Id = id, Name = r.Name ?? "", Pattern = r.Pattern ?? "", Owned = r.Owned, CostPerLicense = r.CostPerLicense, ValidUntil = r.ValidUntil, Notes = r.Notes,
    };

    private static LicenseDto ToDto(LicenseUsage u) => new(u.License.Id, u.License.Name, u.License.Pattern, u.License.Owned, u.Installed, u.Missing,
        u.License.CostPerLicense, u.License.ValidUntil, u.License.Notes, u.Computers, u.Programs);

    private static Report ToReport(int id, SavedReportRequest r) => new()
    {
        Id = id, Name = r.Name ?? "", Description = r.Description, Source = r.Source, CollectionId = r.CollectionId, Days = r.Days,
        Columns = r.Columns is { Count: > 0 } ? string.Join(',', r.Columns) : null, SortColumn = r.SortColumn, SortDescending = r.SortDescending,
    };

    private static SavedReportDto ToDto(Report r) => new(r.Id, r.Name, r.Description, r.Source, r.CollectionId, r.Days, r.ColumnKeys, r.SortColumn, r.SortDescending);

    internal static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Results.Conflict("An entry with this name already exists, or a referenced entry does not exist.");
        }
    }

    private static AuditFilter Filter(string? user, string? action, DateTime? from, DateTime? to, string? q) =>
        new(user, action, Utc(from), Utc(to), q);

    /// <summary>Die Zeitzone des Browsers, wenn die Anfrage aus der Oberfläche kommt; null bei API-Aufrufen ohne Cookie.</summary>
    internal static TimeZoneInfo? Zone(HttpContext http) => BrowserTime.Resolve(http.Request.Cookies[BrowserTime.Cookie]);

    private static DateTime? Utc(DateTime? value) => value is { } v ? v.ToUniversalTime() : null;

    // Mit BOM, damit Excel die Umlaute als UTF-8 erkennt.
    internal static IResult Csv(string content, string fileName) =>
        Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray(), "text/csv; charset=utf-8", fileName);
}
