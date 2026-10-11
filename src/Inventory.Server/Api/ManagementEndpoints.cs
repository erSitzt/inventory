using Inventory.Contracts;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Server.Api;

/// <summary>
/// Schreibende API für Pakete, Sammlungen, Zeitpläne und Deployments: alles, was die Oberfläche anlegt und ändert,
/// lässt sich auch per Skript oder von einem Agenten erledigen. Jede Änderung steht im Protokoll.
/// </summary>
public static class ManagementEndpoints
{
    public sealed record PackageRequest(string Name, string? Version, string? Description, List<StepRequest>? Steps,
        StepConditions? Detection = null, bool VerifyAfterInstall = false);

    /// <param name="NestedPackageId">Gesetzt = dieser Schritt führt ein anderes Paket aus; <paramref name="Definition"/> braucht dann nur den Namen.</param>
    public sealed record StepRequest(JobStep Definition, int? NestedPackageId = null);

    public sealed record StepDto(int Id, int Order, int? NestedPackageId, JobStep Definition);

    public sealed record FileDto(string Path, long Size, DateTime ModifiedUtc);

    public sealed record PackageDetailDto(int Id, string Name, string? Version, string? Description, DateTime ModifiedUtc, string? CatalogId,
        List<StepDto> Steps, List<FileDto> Files, StepConditions? Detection, bool VerifyAfterInstall);

    public sealed record CollectionRequest(string Name, string? Description, bool IsDynamic, bool MatchAll = true, List<CollectionRule>? Rules = null, MaintenanceWindow? MaintenanceWindow = null);

    public sealed record CollectionDto(int Id, string Name, string? Description, bool IsDynamic, bool MatchAll, List<CollectionRule> Rules, int Computers, MaintenanceWindow? MaintenanceWindow = null);

    public sealed record CollectionDetailDto(int Id, string Name, string? Description, bool IsDynamic, bool MatchAll, List<CollectionRule> Rules, List<int> ComputerIds, MaintenanceWindow? MaintenanceWindow = null);

    public sealed record ScheduleRequest(string Name, ScheduleAction Action, ScheduleTrigger Trigger, bool Enabled = true, int? PackageId = null, int? CollectionId = null,
        bool ScanAfter = true, bool RetryOffline = true, DateTime? StartLocal = null, int IntervalMinutes = 60, TimeSpan? TimeOfDay = null, int DaysOfWeek = 0,
        bool RespectMaintenanceWindow = true);

    public sealed record ScheduleDto(int Id, string Name, bool Enabled, ScheduleAction Action, int? PackageId, int? CollectionId, bool ScanAfter, bool RetryOffline,
        ScheduleTrigger Trigger, DateTime? StartLocal, int IntervalMinutes, TimeSpan TimeOfDay, int DaysOfWeek, DateTime? LastRunUtc, DateTime? NextRunUtc, string? LastResult, bool RespectMaintenanceWindow);

    public sealed record DeploymentSummaryDto(int Id, int? PackageId, string PackageName, string? PackageVersion, DateTime CreatedUtc, DateTime? FinishedUtc,
        int Targets, int Succeeded, int AlreadyInstalled, int Failed, int Waiting);

    public sealed record ResultDto(string Result);

    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var packages = app.MapGroup("/api/packages").WithTags("Packages").AddEndpointFilter(MapErrors);

        packages.MapGet("{id:int}", async (int id, PackageService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } package ? Results.Ok(ToDetail(package, service)) : Results.NotFound())
            .WithSummary("One package").WithDescription("Header, steps in execution order and the files in the package folder.").Produces<PackageDetailDto>().Produces(404);

        packages.MapPost("", async (PackageRequest request, PackageService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("name is required");
            if ((request.Steps ?? []).Any(step => step.Definition is null || string.IsNullOrWhiteSpace(step.Definition.Name)))
                throw new ArgumentException("every step needs a definition with a name");
            var package = await service.SaveAsync(null, request.Name, request.Version, request.Description, ct);
            try
            {
                foreach (var step in request.Steps ?? [])
                    await service.SaveStepAsync(package.Id, null, step.Definition, step.NestedPackageId, ct);
                await service.SaveDetectionAsync(package.Id, request.Detection, request.VerifyAfterInstall, ct);
            }
            catch
            {
                // Kein halbes Paket zurücklassen, wenn ein Schritt ungültig ist.
                await service.DeleteAsync(package.Id, CancellationToken.None);
                throw;
            }
            await audit.LogAsync(User(http), "Package created via API", $"{package.Name} ({request.Steps?.Count ?? 0} steps)", ct);
            return Results.Created($"/api/packages/{package.Id}", ToDetail((await service.GetAsync(package.Id, ct))!, service));
        }).WithSummary("Create package").WithDescription("Creates a package, optionally with its steps. Step types: Command, PowerShell, Install, Uninstall (softwareName or productCode, optional arguments), FileCopy, Service, Sleep, Reboot, InventoryScan. Files are uploaded separately with PUT /api/packages/{id}/files/{path}. `detection` tells the runner how to recognise that the package is already installed (softwareInstalled with optional softwareMinVersion, msiProductCode, fileExists, registryKeyExists); such computers finish as AlreadyInstalled without running a step. With `verifyAfterInstall` the detection must also hold after the last step.")
            .Produces<PackageDetailDto>(201).Produces(400);

        packages.MapPut("{id:int}", async (int id, PackageRequest request, PackageService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.SaveAsync(id, request.Name, request.Version, request.Description, ct);
            await service.SaveDetectionAsync(id, request.Detection, request.VerifyAfterInstall, ct);
            await audit.LogAsync(User(http), "Package changed via API", request.Name, ct);
            return Results.Ok(ToDetail((await service.GetAsync(id, ct))!, service));
        }).WithSummary("Change package header").WithDescription("Changes name, version, description and the detection (`detection`, `verifyAfterInstall`; omitting `detection` removes it). Steps are managed through the step endpoints; a `steps` array here is ignored.").Produces<PackageDetailDto>().Produces(404);

        packages.MapDelete("{id:int}", async (int id, PackageService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is not { } package)
                return Results.NotFound();
            await service.DeleteAsync(id, ct);
            await audit.LogAsync(User(http), "Package deleted via API", package.Name, ct);
            return Results.NoContent();
        }).WithSummary("Delete package").WithDescription("Deletes the package including its files. Past deployments stay in the history.").Produces(204).Produces(404);

        packages.MapPost("{id:int}/steps", async (int id, StepRequest request, PackageService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.SaveStepAsync(id, null, request.Definition, request.NestedPackageId, ct);
            await audit.LogAsync(User(http), "Package step created via API", $"Package {id}: {request.Definition.Name}", ct);
            return Results.Ok(ToDetail((await service.GetAsync(id, ct))!, service));
        }).WithSummary("Add step").WithDescription("Appends a step to the package.").Produces<PackageDetailDto>().Produces(400).Produces(404);

        packages.MapPut("{id:int}/steps/{stepId:int}", async (int id, int stepId, StepRequest request, PackageService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.SaveStepAsync(id, stepId, request.Definition, request.NestedPackageId, ct);
            await audit.LogAsync(User(http), "Package step changed via API", $"Package {id}: {request.Definition.Name}", ct);
            return Results.Ok(ToDetail((await service.GetAsync(id, ct))!, service));
        }).WithSummary("Change step").Produces<PackageDetailDto>().Produces(400).Produces(404);

        packages.MapDelete("{id:int}/steps/{stepId:int}", async (int id, int stepId, PackageService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.DeleteStepAsync(id, stepId, ct);
            await audit.LogAsync(User(http), "Package step deleted via API", $"Package {id}: step {stepId}", ct);
            return Results.NoContent();
        }).WithSummary("Delete step").Produces(204);

        packages.MapPost("{id:int}/steps/{stepId:int}/move", async (int id, int stepId, int direction, PackageService service, CancellationToken ct) =>
        {
            await service.MoveStepAsync(id, stepId, Math.Sign(direction), ct);
            return await service.GetAsync(id, ct) is { } package ? Results.Ok(ToDetail(package, service)) : Results.NotFound();
        }).WithSummary("Move step").WithDescription("Query parameter direction: -1 = one position up, 1 = one position down.").Produces<PackageDetailDto>().Produces(404);

        packages.MapGet("{id:int}/msi/{**path}", async (int id, string path, PackageService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is null || !service.Repository.List(id).Any(f => f.RelativePath.Equals(path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) || f.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return Results.NotFound();
            return Inventory.Data.MsiProperties.Read(service.Repository.Resolve(id, path)) is { } product
                ? Results.Ok(product)
                : throw new ArgumentException("the file cannot be read as an MSI");
        }).WithSummary("MSI properties of a package file").WithDescription("Product name, version and product code of an MSI in the package folder – the values for the package detection.")
            .Produces<Inventory.Data.MsiProperties.Product>().Produces(400).Produces(404);

        packages.MapDelete("{id:int}/files/{**path}", async (int id, string path, PackageService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is not { } package)
                return Results.NotFound();
            service.Repository.Delete(id, path);
            await audit.LogAsync(User(http), "Package file deleted via API", $"{package.Name}: {path}", ct);
            return Results.NoContent();
        }).WithSummary("Delete file").Produces(204).Produces(400).Produces(404);

        var deployments = app.MapGroup("/api/deployments").WithTags("Deployments").AddEndpointFilter(MapErrors);

        deployments.MapGet("", async (DeploymentService service, int? take, CancellationToken ct) =>
            (await service.ListAsync(Math.Clamp(take ?? 50, 1, 500), ct)).Select(d => new DeploymentSummaryDto(d.Id, d.PackageId, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc,
                d.Targets.Count, d.Targets.Count(t => t.Status == TargetStatus.Succeeded), d.Targets.Count(t => t.Status == TargetStatus.AlreadyInstalled),
                d.Targets.Count(t => t.Status is TargetStatus.Failed or TargetStatus.Cancelled),
                d.Targets.Count(t => t.Status.IsWaiting()))))
            .WithSummary("Recent deployments").WithDescription("Newest first; query parameter take (default 50, at most 500).");

        deployments.MapPost("{id:int}/cancel", async (int id, DeploymentService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is not { } deployment)
                return Results.NotFound();
            await service.CancelAsync(id);
            await audit.LogAsync(User(http), "Deployment cancelled via API", $"#{id} {deployment.PackageName}", ct);
            return Results.NoContent();
        }).WithSummary("Cancel deployment").WithDescription("Stops running targets and drops targets waiting for an offline computer or a restart.").Produces(204).Produces(404);

        var maintenanceDefault = app.MapGroup("/api/settings/maintenance-window").WithTags("Settings").AddEndpointFilter(MapErrors);

        maintenanceDefault.MapGet("", async (MaintenanceService service, CancellationToken ct) => Results.Json(await service.GetDefaultAsync(ct)))
            .WithSummary("Default maintenance window").WithDescription("Applies to computers that are in no collection with a window of its own; null = no restriction.").Produces<MaintenanceWindow>();

        maintenanceDefault.MapPut("", async (MaintenanceWindow? window, MaintenanceService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.SetDefaultAsync(window, ct);
            await audit.LogAsync(User(http), "Default maintenance window changed via API", "", ct);
            return Results.Json(await service.GetDefaultAsync(ct));
        }).WithSummary("Set default maintenance window").WithDescription("Body: a window ({daysOfWeek, from, to}) or null to remove it.").Produces<MaintenanceWindow>();

        var collections = app.MapGroup("/api/collections").WithTags("Collections").AddEndpointFilter(MapErrors);

        collections.MapGet("", async (CollectionService service, CancellationToken ct) =>
            (await service.ListAsync(ct)).Select(s => new CollectionDto(s.Collection.Id, s.Collection.Name, s.Collection.Description, s.Collection.IsDynamic, s.Collection.MatchAll, s.Collection.Rules, s.Count, s.Collection.MaintenanceWindow)))
            .WithSummary("All collections");

        collections.MapGet("{id:int}", async (int id, CollectionService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } c
                ? Results.Ok(new CollectionDetailDto(c.Id, c.Name, c.Description, c.IsDynamic, c.MatchAll, c.Rules, [.. (await service.GetMemberIdsAsync(id, ct)).Order()], c.MaintenanceWindow))
                : Results.NotFound())
            .WithSummary("One collection").WithDescription("Includes the IDs of the computers that currently belong to it (rules evaluated now for dynamic collections).").Produces<CollectionDetailDto>().Produces(404);

        collections.MapPost("", async (CollectionRequest request, CollectionService service, MaintenanceService maintenance, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var c = await service.SaveAsync(null, request.Name, request.Description, request.IsDynamic, request.MatchAll, request.Rules ?? [], ct);
            await maintenance.SetAsync(c.Id, request.MaintenanceWindow, ct);
            await audit.LogAsync(User(http), "Collection created via API", c.Name, ct);
            return Results.Created($"/api/collections/{c.Id}", new CollectionDto(c.Id, c.Name, c.Description, c.IsDynamic, c.MatchAll, c.Rules, 0, MaintenanceService.Normalize(request.MaintenanceWindow)));
        }).WithSummary("Create collection").WithDescription("Static (members assigned explicitly) or dynamic (rules). matchAll: true = all rules must apply, false = at least one. maintenanceWindow (optional): daysOfWeek as bit mask (bit 0 = Sunday … bit 6 = Saturday), from and to as local server time (\"19:00:00\"); to before from = past midnight.").Produces<CollectionDto>(201).Produces(400);

        collections.MapPut("{id:int}", async (int id, CollectionRequest request, CollectionService service, MaintenanceService maintenance, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var c = await service.SaveAsync(id, request.Name, request.Description, request.IsDynamic, request.MatchAll, request.Rules ?? [], ct);
            await maintenance.SetAsync(id, request.MaintenanceWindow, ct);
            await audit.LogAsync(User(http), "Collection changed via API", c.Name, ct);
            return Results.Ok(new CollectionDto(c.Id, c.Name, c.Description, c.IsDynamic, c.MatchAll, c.Rules, (await service.GetMemberIdsAsync(id, ct)).Count, MaintenanceService.Normalize(request.MaintenanceWindow)));
        }).WithSummary("Change collection").WithDescription("Name, description, matchAll, rules and maintenanceWindow (omitting it removes the window); whether a collection is dynamic cannot be changed afterwards.").Produces<CollectionDto>().Produces(404);

        collections.MapDelete("{id:int}", async (int id, CollectionService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is not { } c)
                return Results.NotFound();
            await service.DeleteAsync(id, ct);
            await audit.LogAsync(User(http), "Collection deleted via API", c.Name, ct);
            return Results.NoContent();
        }).WithSummary("Delete collection").WithDescription("The computers themselves are kept.").Produces(204).Produces(404);

        collections.MapPost("{id:int}/members", async (int id, int[] computerIds, CollectionService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var added = await service.AddMembersAsync(id, computerIds, ct);
            await audit.LogAsync(User(http), "Collection members added via API", $"Collection {id}: {added} computers", ct);
            return Results.Ok(new ResultDto($"{added} added"));
        }).WithSummary("Add members").WithDescription("Body: JSON array of computer IDs. Static collections only.");

        collections.MapDelete("{id:int}/members", async (int id, [Microsoft.AspNetCore.Mvc.FromBody] int[] computerIds, CollectionService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var removed = await service.RemoveMembersAsync(id, computerIds, ct);
            await audit.LogAsync(User(http), "Collection members removed via API", $"Collection {id}: {removed} computers", ct);
            return Results.Ok(new ResultDto($"{removed} removed"));
        }).WithSummary("Remove members").WithDescription("Body: JSON array of computer IDs.");

        var schedules = app.MapGroup("/api/schedules").WithTags("Schedules").AddEndpointFilter(MapErrors);

        schedules.MapGet("", async (ScheduleService service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto))
            .WithSummary("All schedules");

        schedules.MapPost("", async (ScheduleRequest request, ScheduleService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var schedule = Apply(new Schedule { Name = request.Name }, request);
            await service.SaveAsync(schedule, ct);
            await audit.LogAsync(User(http), "Schedule created via API", schedule.Name, ct);
            return Results.Created($"/api/schedules/{schedule.Id}", ToDto(schedule));
        }).WithSummary("Create schedule").WithDescription("action: Scan or Deploy (needs packageId). trigger: Once (startLocal), Interval (intervalMinutes), Daily (timeOfDay) or Weekly (timeOfDay + daysOfWeek bitmask, bit n = day n with Sunday = 0). collectionId null = all computers. Times are server local time.")
            .Produces<ScheduleDto>(201).Produces(400);

        schedules.MapPut("{id:int}", async (int id, ScheduleRequest request, ScheduleService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if ((await service.ListAsync(ct)).FirstOrDefault(s => s.Id == id) is not { } existing)
                return Results.NotFound();
            await service.SaveAsync(Apply(existing, request), ct);
            await audit.LogAsync(User(http), "Schedule changed via API", existing.Name, ct);
            return Results.Ok(ToDto(existing));
        }).WithSummary("Change schedule").Produces<ScheduleDto>().Produces(404);

        schedules.MapDelete("{id:int}", async (int id, ScheduleService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            await audit.LogAsync(User(http), "Schedule deleted via API", $"Schedule {id}", ct);
            return Results.NoContent();
        }).WithSummary("Delete schedule").Produces(204);

        schedules.MapPost("{id:int}/run", async (int id, ScheduleService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await audit.LogAsync(User(http), "Schedule started via API", $"Schedule {id}", ct);
            return Results.Ok(new ResultDto(await service.RunNowAsync(id, ct)));
        }).WithSummary("Run schedule now").WithDescription("Runs the schedule immediately without changing its regular rhythm.");

        return app;
    }

    private static string User(HttpContext http) => http.User.Identity?.Name ?? "(API, not signed in)";

    private static PackageDetailDto ToDetail(Package p, PackageService service) => new(p.Id, p.Name, p.Version, p.Description, p.ModifiedUtc, p.CatalogId,
        [.. p.Steps.OrderBy(s => s.Order).Select(s => new StepDto(s.Id, s.Order, s.NestedPackageId, s.Definition))],
        [.. service.Repository.List(p.Id).Select(f => new FileDto(f.RelativePath, f.Size, f.ModifiedUtc))], p.Detection, p.VerifyAfterInstall);

    private static ScheduleDto ToDto(Schedule s) => new(s.Id, s.Name, s.Enabled, s.Action, s.PackageId, s.CollectionId, s.ScanAfter, s.RetryOffline, s.Trigger,
        s.StartLocal, s.IntervalMinutes, s.TimeOfDay, s.DaysOfWeek, s.LastRunUtc, s.NextRunUtc, s.LastResult, s.RespectMaintenanceWindow);

    private static Schedule Apply(Schedule s, ScheduleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
            throw new ArgumentException("name is required");
        if (r.Action == ScheduleAction.Deploy && r.PackageId is null)
            throw new ArgumentException("packageId is required for action Deploy");
        if (r.Trigger == ScheduleTrigger.Weekly && r.DaysOfWeek == 0)
            throw new ArgumentException("daysOfWeek is required for trigger Weekly");
        if (r.Trigger == ScheduleTrigger.Once && r.StartLocal is null)
            throw new ArgumentException("startLocal is required for trigger Once");
        (s.Name, s.Enabled, s.Action, s.PackageId, s.CollectionId, s.ScanAfter, s.RetryOffline) = (r.Name, r.Enabled, r.Action, r.PackageId, r.CollectionId, r.ScanAfter, r.RetryOffline);
        (s.Trigger, s.StartLocal, s.IntervalMinutes, s.DaysOfWeek) = (r.Trigger, r.StartLocal, Math.Max(r.IntervalMinutes, 1), r.DaysOfWeek);
        s.TimeOfDay = r.TimeOfDay ?? s.TimeOfDay;
        s.RespectMaintenanceWindow = r.RespectMaintenanceWindow;
        return s;
    }

    /// <summary>Übersetzt die Ausnahmen der Dienste in passende Statuscodes statt 500.</summary>
    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict("An entry with this name already exists, or a referenced entry does not exist.");
        }
    }
}
