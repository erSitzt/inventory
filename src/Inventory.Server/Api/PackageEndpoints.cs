using Microsoft.AspNetCore.Authorization;
using Inventory.Contracts;
using Inventory.Core.Model;
using Inventory.Core;
using Inventory.Data.Services;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Http.Features;

namespace Inventory.Server.Api;

public static class PackageEndpoints
{
    public sealed record DeployRequest(int PackageId, int[] ComputerIds, bool ScanAfter, bool RetryOffline = false, bool RespectMaintenanceWindow = false,
        int? MaxFailures = null, int? MaxFailurePercent = null, int? MaxParallel = null);

    public sealed record PackageDto(int Id, string Name, string? Version, string? Description, int Steps, DateTime ModifiedUtc, bool Approved = false);

    public sealed record TargetDto(int Id, int? ComputerId, string ComputerName, TargetStatus Status, string? Error, DateTime? StartedUtc, DateTime? FinishedUtc, List<StepResult> Steps);

    public sealed record DeploymentDto(int Id, int? PackageId, string PackageName, string? PackageVersion, DateTime CreatedUtc, DateTime? FinishedUtc, IEnumerable<TargetDto> Targets, string? StopReason = null);

    public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/packages", async (PackageService packages, CancellationToken ct) =>
            (await packages.ListAsync(ct)).Select(p => new PackageDto(p.Id, p.Name, p.Version, p.Description, p.Steps.Count, p.ModifiedUtc, p.IsApproved)))
            .WithTags("Packages").WithSummary("All packages");

        // Rohdaten im Body, damit sich große Installer per Skript (curl -T) ohne Browser hochladen lassen.
        app.MapPut("/api/packages/{id:int}/files/{**path}", async (int id, string path, HttpContext http, PackageService packages, CancellationToken ct) =>
        {
            if (await packages.GetAsync(id, ct) is null)
                return Results.NotFound();
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
            try
            {
                // Andere Dateien als die freigegebenen: die Freigabe gilt nicht mehr. Zuerst zurückziehen, dann ändern,
                // damit kein gleichzeitig gestartetes Deployment die neue Datei mit der alten Freigabe mitnimmt.
                await packages.RevokeApprovalAsync(id, ct);
                await packages.Repository.SaveAsync(id, path, http.Request.Body, ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            return Results.NoContent();
        }).RequireAdmin().WithTags("Packages").WithSummary("Upload file").WithDescription("Stores the file under the given path in the package; the body is the raw file content (e.g. curl -T setup.msi). Existing files are replaced.")
            .Accepts<byte[]>("application/octet-stream").Produces(204).Produces(404).Produces(400);

        app.MapPost("/api/deployments", async (DeployRequest request, DeploymentService deployments, AuditService audit, HttpContext http, IStringLocalizer<SharedResource> L,
            IAuthorizationService authorization, CancellationToken ct) =>
        {
            try
            {
                // Nur Administratoren dürfen ein noch nicht freigegebenes Paket verteilen.
                var admin = (await authorization.AuthorizeAsync(http.User, Inventory.Server.Services.AccessControl.AdminPolicy)).Succeeded;
                var deployment = await deployments.StartAsync(request.PackageId, request.ComputerIds, new DeploymentOptions(request.ScanAfter, request.RetryOffline, request.RespectMaintenanceWindow,
                    request.MaxFailures, request.MaxFailurePercent, request.MaxParallel) { RequireApproved = !admin }, ct);
                await audit.LogAsync(http.User.Identity?.Name ?? L["(API, not signed in)"], L["Deployment started via API"],
                    L["#{0} {1} on {2}", deployment.Id, deployment.PackageName, string.Join(", ", deployment.Targets.Select(t => t.ComputerName))], ct);
                return Results.Created($"/api/deployments/{deployment.Id}", ToDto(deployment));
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                return Results.BadRequest(ex.Message);
            }
        }).RequireOperator().WithTags("Deployments").WithSummary("Start deployment").WithDescription("Deploys the package to the computers and returns immediately; operators can only deploy approved packages. GET /api/deployments/{id} delivers the progress. ScanAfter: rescan inventory afterwards. RetryOffline: retry unreachable computers for up to 3 days. RespectMaintenanceWindow: computers outside their maintenance window wait until it opens (at most 8 days). MaxFailures / MaxFailurePercent: stop starting further computers once that many failed (the rate counts after five finished and two failed computers); MaxParallel: computers of this deployment running at once.")
            .Produces<DeploymentDto>(201).Produces(400);

        app.MapGet("/api/deployments/{id:int}", async (int id, DeploymentService deployments, CancellationToken ct) =>
            await deployments.GetAsync(id, ct) is { } deployment ? Results.Ok(ToDto(deployment)) : Results.NotFound())
            .WithTags("Deployments").WithSummary("One deployment").WithDescription("Status per computer: Queued, Running, Succeeded, AlreadyInstalled, Failed, Cancelled, WaitingOffline, WaitingReboot or WaitingWindow.").Produces<DeploymentDto>().Produces(404);

        return app;
    }

    private static DeploymentDto ToDto(Deployment d) => new(d.Id, d.PackageId, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc,
        d.Targets.Select(t => new TargetDto(t.Id, t.ComputerId, t.ComputerName, t.Status, t.Error, t.StartedUtc, t.FinishedUtc, t.Steps)), d.StopReason);
}
