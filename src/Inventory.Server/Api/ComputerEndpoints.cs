using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;

namespace Inventory.Server.Api;

public static class ComputerEndpoints
{
    public sealed record ComputerDto(int Id, string Name, string? Description, string? IpAddress, bool IsOnline,
        DateTime? LastSeenUtc, DateTime? LastScanUtc, string? LastScanError, TransportKind? Transport, int? CredentialId,
        string? ReportedName, string? ReverseDnsName, bool NameMismatch, string? OsName, string? OsVersion, string? Manufacturer, string? Model, string? SerialNumber, long? MemoryBytes, string? LoggedOnUser);

    public sealed record ImportRequest(string Names);

    public sealed record ProbeResponse(int Online);

    public sealed record SoftwareDto(string Name, string? Version, string? Publisher, string? InstallDate, bool Is64Bit, bool PerUser);

    public sealed record RunRequest(StepType Type, string Script, int? TimeoutSeconds, StepRunAs RunAs = StepRunAs.System);

    public static IEndpointRouteBuilder MapComputerEndpoints(this IEndpointRouteBuilder app)
    {
        // Starter für Admin-PCs: öffnet Fernzugriffe direkt aus der Oberfläche (inventory://-Links).
        app.MapGet("/download/launcher", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "launcher", "InventoryLauncher.exe");
            return File.Exists(path) ? Results.File(path, "application/octet-stream", "InventoryLauncher.exe") : Results.NotFound();
        }).WithTags("Downloads").WithSummary("Starter für Admin-PCs").WithDescription("InventoryLauncher.exe: registriert inventory://-Links, über die die Oberfläche Remoteunterstützung, RDP und RDP-Spiegelung direkt öffnet.");

        var group = app.MapGroup("/api/computers").WithTags("Rechner");

        group.MapGet("", async (ComputerService computers, CancellationToken ct) =>
            (await computers.ListAsync(ct)).Select(ToDto))
            .WithSummary("Alle Rechner").WithDescription("Stammdaten, Status und die wichtigsten Inventarwerte aller Rechner.");

        group.MapGet("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetAsync(id, ct) is { } computer ? Results.Ok(ToDto(computer)) : Results.NotFound())
            .WithSummary("Ein Rechner").Produces<ComputerDto>().Produces(404);

        group.MapPost("", async (ImportRequest request, ComputerService computers, CancellationToken ct) =>
            (await computers.ImportAsync(request.Names, ct)).Select(ToDto))
            .WithSummary("Rechner anlegen").WithDescription("Namen durch Zeilenumbruch, Komma, Semikolon oder Leerzeichen getrennt. Liefert nur die neu angelegten Rechner; vorhandene bleiben unverändert.");

        group.MapDelete("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.DeleteAsync([id], ct) > 0 ? Results.NoContent() : Results.NotFound())
            .WithSummary("Rechner löschen").WithDescription("Löscht den Rechner samt Inventardaten und Verlauf.").Produces(204).Produces(404);

        group.MapPost("probe", async (int[]? ids, ComputerService computers, CancellationToken ct) =>
            new ProbeResponse(await computers.ProbeAsync(ids is { Length: > 0 } ? ids : null, ct)))
            .WithSummary("Erreichbarkeit prüfen").WithDescription("Body: JSON-Array mit Rechner-IDs; leer oder null = alle Rechner. Liefert die Zahl der erreichbaren Rechner.");

        group.MapPost("{id:int}/scan", async (int id, ScanService scans, CancellationToken ct) => await scans.ScanAsync(id, ct))
            .WithSummary("Inventar-Scan").WithDescription("Scannt den Rechner sofort und wartet auf das Ergebnis (je nach Transport einige Sekunden bis Minuten).");

        group.MapGet("{id:int}/software", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetDetailAsync(id, ct) is { } computer
                ? Results.Ok(computer.Software.OrderBy(s => s.Name).Select(s => new SoftwareDto(s.Name, s.Version, s.Publisher, s.InstallDate, s.Is64Bit, s.PerUser)))
                : Results.NotFound())
            .WithSummary("Installierte Software").Produces<IEnumerable<SoftwareDto>>().Produces(404);

        // Lädt eine .rdp-Datei; Windows öffnet sie direkt mit der Remotedesktopverbindung.
        group.MapGet("{id:int}/rdp", async (int id, ComputerService computers, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await computers.GetAsync(id, ct) is not { } computer)
                return Results.NotFound();
            await audit.LogAsync(http.User.Identity?.Name ?? "(ohne Anmeldung)", "Fernzugriff: Remotedesktop", computer.Name, ct);
            return Results.File(System.Text.Encoding.ASCII.GetBytes(RemoteAccessLinks.RdpFile(computer.Name)), "application/x-rdp", $"{computer.Name}.rdp");
        }).WithSummary("RDP-Datei").WithDescription("Verbindungsdatei für die Remotedesktopverbindung; der Abruf wird protokolliert.").Produces(404);

        group.MapPost("{id:int}/run", async (int id, RunRequest request, RemoteJobService jobs, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await audit.LogAsync(http.User.Identity?.Name ?? "(API ohne Anmeldung)", $"Remote-Befehl über API ({request.Type})", $"Rechner {id}: {request.Script}", ct);
            var job = new JobManifest
            {
                Steps = [new JobStep { Name = "Befehl", Type = request.Type, Script = request.Script, TimeoutSeconds = request.TimeoutSeconds ?? 600, RunAs = request.RunAs }],
            };
            return await jobs.RunAsync(id, job, null, ct);
        }).WithSummary("Befehl ausführen").WithDescription("Führt ein Kommando (Type = Command) oder PowerShell-Skript (Type = PowerShell) auf dem Rechner aus und wartet auf das Ergebnis. RunAs: System (Standard) oder LoggedOnUser. Wird protokolliert.");

        return app;
    }

    private static ComputerDto ToDto(Computer c) =>
        new(c.Id, c.Name, c.Description, c.IpAddress, c.IsOnline, c.LastSeenUtc, c.LastScanUtc, c.LastScanError, c.Transport, c.CredentialId,
            c.ReportedName, c.ReverseDnsName, c.NameMismatch, c.OsName, c.OsVersion, c.Manufacturer, c.Model, c.SerialNumber, c.MemoryBytes, c.LoggedOnUser);
}
