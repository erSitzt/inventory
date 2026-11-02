using Inventory.Server.Plugins;
using Inventory.Server.Services;

namespace Inventory.Server.Api;

public sealed record PluginDto(string Folder, string? Id, string? Name, string? Version, string? ApiVersion, PluginState State, string? Error, string? Publisher, string Sha256,
    IReadOnlyList<string> Contributions);

public sealed record PluginActionDto(string Id, string Title, string? Plugin, string MinRole, string? Confirm);

public sealed record PluginActionRequest(int[] ComputerIds);

public sealed record PluginActionResultDto(bool Success, string Message);

public static class PluginEndpoints
{
    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/plugins").WithTags("Plugins");

        group.MapGet("", (PluginCatalog catalog) => catalog.Plugins.Select(p => new PluginDto(p.Folder, p.Id, p.Name, p.FileVersion, p.ApiVersion, p.State, p.Error, p.Publisher,
                p.Sha256, p.Contributions)))
            .RequireAdmin().WithSummary("Installed plugins").WithDescription("Every folder under plugins with its state (Disabled, Changed, Loaded, Failed, Incompatible). Administrators only.");

        group.MapGet("actions", async (PluginActions actions, HttpContext http) =>
                (await actions.AvailableAsync(http.User)).Select(a => new PluginActionDto(a.Id, a.Title, actions.OwnerName(a), a.MinRole.ToString(), a.Confirm)))
            .WithSummary("Plugin actions").WithDescription("Actions of the loaded plugins that the caller may run.");

        group.MapPost("actions/{id}", async (string id, PluginActionRequest request, PluginActions actions, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var result = await actions.RunAsync(id, request.ComputerIds ?? [], http.User, http.Actor(), ct);
                return Results.Ok(new PluginActionResultDto(result.Success, result.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).RequireOperator().WithSummary("Run a plugin action").WithDescription("Runs the action for the given computers and waits for its result. The action's own minimum role and the operator's area apply; the call is logged.");

        return app;
    }
}
