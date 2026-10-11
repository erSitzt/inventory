using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Api;

public static class ComputerEndpoints
{
    public sealed record ComputerDto(int Id, string Name, string? Description, string? IpAddress, bool IsOnline,
        DateTime? LastSeenUtc, DateTime? LastScanUtc, string? LastScanError, TransportKind? Transport, int? CredentialId,
        string? ReportedName, string? ReverseDnsName, bool NameMismatch, string? OsName, string? OsVersion, string? Manufacturer, string? Model, string? SerialNumber, long? MemoryBytes, string? LoggedOnUser);

    public sealed record ImportRequest(string Names);

    public sealed record ProbeResponse(int Online);

    public sealed record SoftwareDto(string Name, string? Version, string? Publisher, string? InstallDate, bool Is64Bit, bool PerUser, bool CarriedOver);

    public sealed record RunRequest(StepType Type, string Script, int? TimeoutSeconds, StepRunAs RunAs = StepRunAs.System);

    public static IEndpointRouteBuilder MapComputerEndpoints(this IEndpointRouteBuilder app)
    {
        // Starter für Admin-PCs: öffnet Fernzugriffe direkt aus der Oberfläche (inventory://-Links).
        app.MapGet("/download/launcher", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "launcher", "InventoryLauncher.exe");
            return File.Exists(path) ? Results.File(path, "application/octet-stream", "InventoryLauncher.exe") : Results.NotFound();
        }).WithTags("Downloads").WithSummary("Launcher for admin PCs").WithDescription("InventoryLauncher.exe: registers inventory:// links through which the UI opens Remote Assistance, RDP and RDP shadowing directly.");

        var group = app.MapGroup("/api/computers").WithTags("Computers");

        group.MapGet("", async (ComputerService computers, CancellationToken ct) =>
            (await computers.ListAsync(ct)).Select(ToDto))
            .WithSummary("All computers").WithDescription("Master data, status and the most important inventory values of all computers.");

        group.MapGet("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetAsync(id, ct) is { } computer ? Results.Ok(ToDto(computer)) : Results.NotFound())
            .WithSummary("One computer").Produces<ComputerDto>().Produces(404);

        group.MapPost("", async (ImportRequest request, ComputerService computers, CancellationToken ct) =>
            (await computers.ImportAsync(request.Names, ct)).Select(ToDto))
            .WithSummary("Create computers").WithDescription("Names separated by newline, comma, semicolon or space. Returns only the newly created computers; existing ones stay unchanged.");

        group.MapDelete("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.DeleteAsync([id], ct) > 0 ? Results.NoContent() : Results.NotFound())
            .WithSummary("Delete computer").WithDescription("Deletes the computer including its inventory data and history.").Produces(204).Produces(404);

        group.MapPost("probe", async (int[]? ids, ComputerService computers, CancellationToken ct) =>
            new ProbeResponse(await computers.ProbeAsync(ids is { Length: > 0 } ? ids : null, ct)))
            .WithSummary("Check reachability").WithDescription("Body: JSON array of computer IDs; empty or null = all computers. Returns the number of reachable computers.");

        group.MapPost("{id:int}/scan", async (int id, ScanService scans, CancellationToken ct) => await scans.ScanAsync(id, ct))
            .WithSummary("Inventory scan").WithDescription("Scans the computer immediately and waits for the result (seconds to minutes depending on the transport).");

        group.MapGet("{id:int}/software", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetDetailAsync(id, ct) is { } computer
                ? Results.Ok(computer.Software.OrderBy(s => s.Name).Select(s => new SoftwareDto(s.Name, s.Version, s.Publisher, s.InstallDate, s.Is64Bit, s.PerUser, s.CarriedOver)))
                : Results.NotFound())
            .WithSummary("Installed software").WithDescription("perUser = installed in one user's profile only. carriedOver = kept from an earlier scan because that user was not logged on during the last scan.").Produces<IEnumerable<SoftwareDto>>().Produces(404);

        // Lädt eine .rdp-Datei; Windows öffnet sie direkt mit der Remotedesktopverbindung.
        group.MapGet("{id:int}/rdp", async (int id, ComputerService computers, AuditService audit, HttpContext http, IStringLocalizer<SharedResource> L, CancellationToken ct) =>
        {
            if (await computers.GetAsync(id, ct) is not { } computer)
                return Results.NotFound();
            await audit.LogAsync(http.User.Identity?.Name ?? L["(not signed in)"], L["Remote access: Remote Desktop"], computer.Name, ct);
            return Results.File(System.Text.Encoding.ASCII.GetBytes(RemoteAccessLinks.RdpFile(computer.Name)), "application/x-rdp", $"{computer.Name}.rdp");
        }).WithSummary("RDP file").WithDescription("Connection file for Remote Desktop Connection; the download is logged.").Produces(404);

        group.MapPost("{id:int}/run", async (int id, RunRequest request, RemoteJobService jobs, AuditService audit, HttpContext http, IStringLocalizer<SharedResource> L, CancellationToken ct) =>
        {
            await audit.LogAsync(http.User.Identity?.Name ?? L["(API, not signed in)"], L["Remote command via API ({0})", request.Type], $"{L["Computer"]} {id}: {request.Script}", ct);
            var job = new JobManifest
            {
                Steps = [new JobStep { Name = L["Command"], Type = request.Type, Script = request.Script, TimeoutSeconds = request.TimeoutSeconds ?? 600, RunAs = request.RunAs }],
            };
            return await jobs.RunAsync(id, job, null, ct);
        }).WithSummary("Run command").WithDescription("Runs a command (Type = Command) or PowerShell script (Type = PowerShell) on the computer and waits for the result. RunAs: System (default) or LoggedOnUser. Logged.");

        return app;
    }

    private static ComputerDto ToDto(Computer c) =>
        new(c.Id, c.Name, c.Description, c.IpAddress, c.IsOnline, c.LastSeenUtc, c.LastScanUtc, c.LastScanError, c.Transport, c.CredentialId,
            c.ReportedName, c.ReverseDnsName, c.NameMismatch, c.OsName, c.OsVersion, c.Manufacturer, c.Model, c.SerialNumber, c.MemoryBytes, c.LoggedOnUser);
}
