using System.Text;
using Inventory.Data.Services;

namespace Inventory.Server.Api;

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

        return app;
    }

    // Mit BOM, damit Excel die Umlaute als UTF-8 erkennt.
    private static IResult Csv(string content, string fileName) =>
        Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray(), "text/csv; charset=utf-8", fileName);
}
