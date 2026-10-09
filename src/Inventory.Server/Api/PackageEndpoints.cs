using Inventory.Contracts;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Inventory.Server.Api;

public static class PackageEndpoints
{
    public sealed record DeployRequest(int PackageId, int[] ComputerIds, bool ScanAfter, bool RetryOffline = false);

    public sealed record PackageDto(int Id, string Name, string? Version, string? Description, int Steps, DateTime ModifiedUtc);

    public sealed record TargetDto(int Id, int? ComputerId, string ComputerName, TargetStatus Status, string? Error, DateTime? StartedUtc, DateTime? FinishedUtc, List<StepResult> Steps);

    public sealed record DeploymentDto(int Id, int? PackageId, string PackageName, string? PackageVersion, DateTime CreatedUtc, DateTime? FinishedUtc, IEnumerable<TargetDto> Targets);

    public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/packages", async (PackageService packages, CancellationToken ct) =>
            (await packages.ListAsync(ct)).Select(p => new PackageDto(p.Id, p.Name, p.Version, p.Description, p.Steps.Count, p.ModifiedUtc)))
            .WithTags("Pakete").WithSummary("Alle Pakete");

        // Rohdaten im Body, damit sich große Installer per Skript (curl -T) ohne Browser hochladen lassen.
        app.MapPut("/api/packages/{id:int}/files/{**path}", async (int id, string path, HttpContext http, PackageService packages, CancellationToken ct) =>
        {
            if (await packages.GetAsync(id, ct) is null)
                return Results.NotFound();
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
            try
            {
                await packages.Repository.SaveAsync(id, path, http.Request.Body, ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            return Results.NoContent();
        }).WithTags("Pakete").WithSummary("Datei hochladen").WithDescription("Legt die Datei unter dem angegebenen Pfad im Paket ab; der Body ist der rohe Dateiinhalt (z. B. curl -T setup.msi). Vorhandene Dateien werden ersetzt.")
            .Accepts<byte[]>("application/octet-stream").Produces(204).Produces(404).Produces(400);

        app.MapPost("/api/deployments", async (DeployRequest request, DeploymentService deployments, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var deployment = await deployments.StartAsync(request.PackageId, request.ComputerIds, request.ScanAfter, request.RetryOffline, ct);
                await audit.LogAsync(http.User.Identity?.Name ?? "(API ohne Anmeldung)", "Deployment über API gestartet",
                    $"#{deployment.Id} {deployment.PackageName} auf {string.Join(", ", deployment.Targets.Select(t => t.ComputerName))}", ct);
                return Results.Created($"/api/deployments/{deployment.Id}", ToDto(deployment));
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                return Results.BadRequest(ex.Message);
            }
        }).WithTags("Deployments").WithSummary("Deployment starten").WithDescription("Stellt das Paket auf den Rechnern bereit und kehrt sofort zurück; den Fortschritt liefert GET /api/deployments/{id}. ScanAfter: danach Inventar neu scannen. RetryOffline: nicht erreichbare Rechner bis zu 3 Tage nachholen.")
            .Produces<DeploymentDto>(201).Produces(400);

        app.MapGet("/api/deployments/{id:int}", async (int id, DeploymentService deployments, CancellationToken ct) =>
            await deployments.GetAsync(id, ct) is { } deployment ? Results.Ok(ToDto(deployment)) : Results.NotFound())
            .WithTags("Deployments").WithSummary("Ein Deployment").WithDescription("Status je Rechner: Queued, Running, Succeeded, Failed, Cancelled oder WaitingOffline.").Produces<DeploymentDto>().Produces(404);

        return app;
    }

    private static DeploymentDto ToDto(Deployment d) => new(d.Id, d.PackageId, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc,
        d.Targets.Select(t => new TargetDto(t.Id, t.ComputerId, t.ComputerName, t.Status, t.Error, t.StartedUtc, t.FinishedUtc, t.Steps)));
}
