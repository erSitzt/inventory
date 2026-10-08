using Inventory.Contracts;
using Inventory.Core.Model;
using Inventory.Data.Services;

namespace Inventory.Server.Api;

public static class ComputerEndpoints
{
    public sealed record ComputerDto(int Id, string Name, string? Description, string? IpAddress, bool IsOnline,
        DateTime? LastSeenUtc, DateTime? LastScanUtc, string? LastScanError, TransportKind? Transport, int? CredentialId,
        string? ReportedName, string? ReverseDnsName, bool NameMismatch, string? OsName, string? OsVersion, string? Manufacturer, string? Model, string? SerialNumber, long? MemoryBytes, string? LoggedOnUser);

    public sealed record ImportRequest(string Names);

    public sealed record RunRequest(StepType Type, string Script, int? TimeoutSeconds, StepRunAs RunAs = StepRunAs.System);

    public static IEndpointRouteBuilder MapComputerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/computers");

        group.MapGet("", async (ComputerService computers, CancellationToken ct) =>
            (await computers.ListAsync(ct)).Select(ToDto));

        group.MapGet("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetAsync(id, ct) is { } computer ? Results.Ok(ToDto(computer)) : Results.NotFound());

        group.MapPost("", async (ImportRequest request, ComputerService computers, CancellationToken ct) =>
            (await computers.ImportAsync(request.Names, ct)).Select(ToDto));

        group.MapDelete("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.DeleteAsync([id], ct) > 0 ? Results.NoContent() : Results.NotFound());

        group.MapPost("probe", async (int[]? ids, ComputerService computers, CancellationToken ct) =>
            new { online = await computers.ProbeAsync(ids is { Length: > 0 } ? ids : null, ct) });

        group.MapPost("{id:int}/scan", async (int id, ScanService scans, CancellationToken ct) => await scans.ScanAsync(id, ct));

        group.MapGet("{id:int}/software", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetDetailAsync(id, ct) is { } computer
                ? Results.Ok(computer.Software.OrderBy(s => s.Name).Select(s => new { s.Name, s.Version, s.Publisher, s.InstallDate, s.Is64Bit, s.PerUser }))
                : Results.NotFound());

        group.MapPost("{id:int}/run", async (int id, RunRequest request, RemoteJobService jobs, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await audit.LogAsync(http.User.Identity?.Name ?? "(API ohne Anmeldung)", $"Remote-Befehl über API ({request.Type})", $"Rechner {id}: {request.Script}", ct);
            var job = new JobManifest
            {
                Steps = [new JobStep { Name = "Befehl", Type = request.Type, Script = request.Script, TimeoutSeconds = request.TimeoutSeconds ?? 600, RunAs = request.RunAs }],
            };
            return await jobs.RunAsync(id, job, null, ct);
        });

        return app;
    }

    private static ComputerDto ToDto(Computer c) =>
        new(c.Id, c.Name, c.Description, c.IpAddress, c.IsOnline, c.LastSeenUtc, c.LastScanUtc, c.LastScanError, c.Transport, c.CredentialId,
            c.ReportedName, c.ReverseDnsName, c.NameMismatch, c.OsName, c.OsVersion, c.Manufacturer, c.Model, c.SerialNumber, c.MemoryBytes, c.LoggedOnUser);
}
