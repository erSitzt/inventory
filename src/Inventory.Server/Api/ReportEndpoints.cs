using Inventory.Core;
using System.Text;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Api;

public sealed record AuditDto(long Id, DateTime TimeUtc, string User, AccessRole? Role, AuditSource Source, string Action, string? Details);

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports/computers.csv", async (ReportService reports, CancellationToken ct) =>
            Csv(await reports.ComputersCsvAsync(ct), "rechner.csv"))
            .WithTags("Reports").WithSummary("Computers as CSV").WithDescription("All computers with inventory and security status; UTF-8 with BOM, opens directly in Excel.").Produces<string>(200, "text/csv");

        app.MapGet("/api/reports/software.csv", async (ReportService reports, CancellationToken ct) =>
            Csv(await reports.SoftwareCsvAsync(ct), "software.csv"))
            .WithTags("Reports").WithSummary("Software as CSV").WithDescription("One row per computer and installed program.").Produces<string>(200, "text/csv");

        app.MapGet("/api/reports/software", async (ReportService reports, CancellationToken ct) => await reports.SoftwareAsync(ct))
            .WithTags("Reports").WithSummary("Software overview").WithDescription("Every combination of program and version with the number of computers it is installed on.");

        var audit = app.MapGroup("/api/audit").WithTags("Audit log").RequireOperator();

        audit.MapGet("", async (AuditService service, string? user, string? action, DateTime? from, DateTime? to, string? q, int? take, CancellationToken ct) =>
            (await service.ListAsync(Math.Clamp(take ?? 500, 1, 5000), ct, Filter(user, action, from, to, q)))
                .Select(e => new AuditDto(e.Id, DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc), e.User, e.Role, e.Source, e.Action, e.Details)))
            .WithSummary("Audit log entries").WithDescription("Newest first. Optional filters: user and action (exact), from and to (to is exclusive; values without a time zone count as server local time), q (text in user, action or details), take (default 500, at most 5000).");

        audit.MapGet("export.csv", async (AuditService service, HttpContext http, IStringLocalizer<SharedResource> L, string? user, string? action, DateTime? from, DateTime? to, string? q, CancellationToken ct) =>
        {
            var content = await service.CsvAsync(L, Filter(user, action, from, to, q), ct);
            await service.LogAsync(http.Actor(), L["Audit log exported"], http.Request.QueryString.HasValue ? Uri.UnescapeDataString(http.Request.QueryString.Value!.TrimStart('?')) : null, ct);
            return Csv(content, "audit.csv");
        }).WithSummary("Audit log as CSV").WithDescription($"Same filters as the list; at most {AuditService.MaxExportRows} rows, newest first. The export itself is logged.").Produces<string>(200, "text/csv");

        return app;
    }

    private static AuditFilter Filter(string? user, string? action, DateTime? from, DateTime? to, string? q) =>
        new(user, action, Utc(from), Utc(to), q);

    private static DateTime? Utc(DateTime? value) => value is { } v ? v.ToUniversalTime() : null;

    // Mit BOM, damit Excel die Umlaute als UTF-8 erkennt.
    private static IResult Csv(string content, string fileName) =>
        Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray(), "text/csv; charset=utf-8", fileName);
}
