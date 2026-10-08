using Inventory.Core.Model;
using Inventory.Data.Services;

namespace Inventory.Server.Api;

public static class ComputerEndpoints
{
    public sealed record ComputerDto(int Id, string Name, string? Description, string? IpAddress, bool IsOnline,
        DateTime? LastSeenUtc, DateTime? LastScanUtc, TransportKind? Transport, int? CredentialId);

    public sealed record ImportRequest(string Names);

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

        return app;
    }

    private static ComputerDto ToDto(Computer c) =>
        new(c.Id, c.Name, c.Description, c.IpAddress, c.IsOnline, c.LastSeenUtc, c.LastScanUtc, c.Transport, c.CredentialId);
}
