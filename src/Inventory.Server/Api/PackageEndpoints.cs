using Inventory.Server.Services;
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
        int? MaxFailures = null, int? MaxFailurePercent = null, int? MaxParallel = null, bool WakeTargets = false,
        int? PilotCollectionId = null, int PilotHoldHours = 24, int PilotMaxFailurePercent = 0);

    public sealed record PackageDto(int Id, string Name, string? Version, string? Description, int Steps, DateTime ModifiedUtc, bool Approved = false);

    public sealed record TargetDto(int Id, int? ComputerId, string ComputerName, TargetStatus Status, string? Error, DateTime? StartedUtc, DateTime? FinishedUtc, List<StepResult> Steps, bool Pilot = false);

    public sealed record DeploymentDto(int Id, int? PackageId, string PackageName, string? PackageVersion, DateTime CreatedUtc, DateTime? FinishedUtc, IEnumerable<TargetDto> Targets, string? StopReason = null);

    public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/packages", async (PackageService packages, CancellationToken ct) =>
            (await packages.ListAsync(ct)).Select(p => new PackageDto(p.Id, p.Name, p.Version, p.Description, p.Steps.Count, p.ModifiedUtc, p.IsApproved)))
            .WithTags("Packages").WithSummary("All packages");

        // Rohdaten im Body, damit sich große Installer per Skript (curl -T) ohne Browser hochladen lassen.
        app.MapPut("/api/packages/{id:int}/files/{**path}", async (int id, string path, HttpContext http, PackageService packages, IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (await packages.GetAsync(id, ct) is not { } package)
                return Results.NotFound();
            // Operatoren nur in Pakete, die ein KI-Assistent angelegt hat (sie warten ohnehin auf eine Freigabe in der Oberfläche).
            if (!package.IsAiGenerated && !(await authorization.AuthorizeAsync(http.User, Inventory.Server.Services.AccessControl.AdminPolicy)).Succeeded)
                return Results.Problem("Operators can only upload files to packages created by an AI assistant.", statusCode: StatusCodes.Status403Forbidden);
            // Große Installer: ohne Größengrenze (wo der Server sie überhaupt anbietet und noch ändern lässt).
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = null;
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
        }).RequireOperator().WithTags("Packages").WithSummary("Upload file").WithDescription("Stores the file under the given path in the package; the body is the raw file content (e.g. curl -T setup.msi). Existing files are replaced and the approval is withdrawn. Administrators; operators only for packages created by an AI assistant.")
            .Accepts<byte[]>("application/octet-stream").Produces(204).Produces(404).Produces(400);

        app.MapPost("/api/deployments", async (DeployRequest request, DeploymentService deployments, PackageService packages, AuditService audit, HttpContext http, IStringLocalizer<SharedResource> L,
            IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (await Inventory.Server.Services.ComputerScope.ForbiddenAsync(http, request.ComputerIds) is { } forbidden)
                return forbidden;
            try
            {
                // Nur Administratoren dürfen ein noch nicht freigegebenes Paket verteilen.
                // Ein Paket eines KI-Assistenten läuft nie ohne Freigabe in der Oberfläche, auch nicht mit Administrator-Token.
                var admin = (await authorization.AuthorizeAsync(http.User, Inventory.Server.Services.AccessControl.AdminPolicy)).Succeeded
                    && (await packages.GetAsync(request.PackageId, ct))?.IsAiGenerated != true;
                var deployment = await deployments.StartAsync(request.PackageId, request.ComputerIds, new DeploymentOptions(request.ScanAfter, request.RetryOffline, request.RespectMaintenanceWindow,
                    request.MaxFailures, request.MaxFailurePercent, request.MaxParallel) {
                    RequireApproved = !admin, Wake = request.WakeTargets,
                    PilotCollectionId = request.PilotCollectionId, PilotHoldHours = request.PilotHoldHours, PilotMaxFailurePercent = request.PilotMaxFailurePercent,
                }, ct);
                await audit.LogAsync(http.Actor(), L["Deployment started via API"],
                    L["#{0} {1} on {2}", deployment.Id, deployment.PackageName, string.Join(", ", deployment.Targets.Select(t => t.ComputerName))], ct);
                return Results.Created($"/api/deployments/{deployment.Id}", ToDto(deployment));
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                return Results.BadRequest(ex.Message);
            }
        }).RequireOperator().WithTags("Deployments").WithSummary("Start deployment").WithDescription("Deploys the package to the computers and returns immediately; operators can only deploy approved packages. GET /api/deployments/{id} delivers the progress. ScanAfter: rescan inventory afterwards. RetryOffline: retry unreachable computers for up to 3 days. RespectMaintenanceWindow: computers outside their maintenance window wait until it opens (at most 8 days). MaxFailures / MaxFailurePercent: stop starting further computers once that many failed (the rate counts after five finished and two failed computers); MaxParallel: computers of this deployment running at once. WakeTargets: send Wake-on-LAN to unreachable computers; implies RetryOffline. PilotCollectionId: phased rollout – the targets that are members of this collection run first, the others follow PilotHoldHours (default 24) after the last of them finished, unless more than PilotMaxFailurePercent (default 0) of the pilot group failed.")
            .Produces<DeploymentDto>(201).Produces(400);

        app.MapGet("/api/deployments/{id:int}", async (int id, DeploymentService deployments, CancellationToken ct) =>
            await deployments.GetAsync(id, ct) is { } deployment ? Results.Ok(ToDto(deployment)) : Results.NotFound())
            .WithTags("Deployments").WithSummary("One deployment").WithDescription("Status per computer: Queued, Running, Succeeded, AlreadyInstalled, Failed, Cancelled, WaitingOffline, WaitingReboot, WaitingWindow or WaitingPilot, WaitingUser (the logged-on user postponed the start).").Produces<DeploymentDto>().Produces(404);

        app.MapGet("/api/deployment-requests", async (DeploymentRequestService requests, bool? pendingOnly, CancellationToken ct) =>
            (await requests.ListAsync(pendingOnly ?? false, 100, ct)).Select(ToDto))
            .WithTags("Deployments").WithSummary("Deployment requests").WithDescription("Deployments that AI assistants (MCP) asked for and that wait for, or got, a decision in the web interface. Newest first.");

        app.MapGet("/api/deployment-requests/{id:int}", async (int id, DeploymentRequestService requests, CancellationToken ct) =>
            await requests.GetAsync(id, ct) is { } request ? Results.Ok(ToDto(request)) : Results.NotFound())
            .WithTags("Deployments").WithSummary("One deployment request").WithDescription("status: Pending, Started (deploymentId set) or Rejected (note = reason).");

        return app;
    }

    public sealed record DeploymentRequestDto(int Id, int PackageId, string? PackageName, IReadOnlyList<int> ComputerIds, string RequestedBy, DateTime RequestedUtc, string? Reason,
        DeploymentRequestStatus Status, string? DecidedBy, DateTime? DecidedUtc, string? Note, int? DeploymentId);

    internal static DeploymentRequestDto ToDto(DeploymentRequest r) => new(r.Id, r.PackageId, r.Package?.Name, r.ComputerIds, r.RequestedBy,
        DateTime.SpecifyKind(r.RequestedUtc, DateTimeKind.Utc), r.Reason, r.Status, r.DecidedBy, r.DecidedUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null, r.Note, r.DeploymentId);

    internal static DeploymentDto ToDto(Deployment d) => new(d.Id, d.PackageId, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc,
        d.Targets.Select(t => new TargetDto(t.Id, t.ComputerId, t.ComputerName, t.Status, t.Error, t.StartedUtc, t.FinishedUtc, t.Steps, t.Pilot)), d.StopReason);
}
