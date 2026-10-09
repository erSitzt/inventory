using System.Text;
using Inventory.Data.Services;

namespace Inventory.Server.Api;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports/computers.csv", async (ReportService reports, CancellationToken ct) =>
            Csv(await reports.ComputersCsvAsync(ct), "rechner.csv"))
            .WithTags("Berichte").WithSummary("Rechner als CSV").WithDescription("Alle Rechner mit Inventar- und Sicherheitsstatus; UTF-8 mit BOM, direkt in Excel zu öffnen.").Produces<string>(200, "text/csv");

        app.MapGet("/api/reports/software.csv", async (ReportService reports, CancellationToken ct) =>
            Csv(await reports.SoftwareCsvAsync(ct), "software.csv"))
            .WithTags("Berichte").WithSummary("Software als CSV").WithDescription("Eine Zeile je Rechner und installiertem Programm.").Produces<string>(200, "text/csv");

        app.MapGet("/api/reports/software", async (ReportService reports, CancellationToken ct) => await reports.SoftwareAsync(ct))
            .WithTags("Berichte").WithSummary("Software-Übersicht").WithDescription("Jede Kombination aus Programm und Version mit der Zahl der Rechner, auf denen sie installiert ist.");

        return app;
    }

    // Mit BOM, damit Excel die Umlaute als UTF-8 erkennt.
    private static IResult Csv(string content, string fileName) =>
        Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray(), "text/csv; charset=utf-8", fileName);
}
