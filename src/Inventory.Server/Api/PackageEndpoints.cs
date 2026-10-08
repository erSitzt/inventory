using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Inventory.Server.Api;

public static class PackageEndpoints
{
    public sealed record DeployRequest(int PackageId, int[] ComputerIds, bool ScanAfter, bool RetryOffline = false);

    public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/packages", async (PackageService packages, CancellationToken ct) =>
            (await packages.ListAsync(ct)).Select(p => new { p.Id, p.Name, p.Version, p.Description, Steps = p.Steps.Count, p.ModifiedUtc }));

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
        });

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
        });

        app.MapGet("/api/deployments/{id:int}", async (int id, DeploymentService deployments, CancellationToken ct) =>
            await deployments.GetAsync(id, ct) is { } deployment ? Results.Ok(ToDto(deployment)) : Results.NotFound());

        return app;
    }

    private static object ToDto(Deployment d) => new
    {
        d.Id, d.PackageId, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc,
        Targets = d.Targets.Select(t => new { t.Id, t.ComputerId, t.ComputerName, t.Status, t.Error, t.StartedUtc, t.FinishedUtc, t.Steps }),
    };
}
