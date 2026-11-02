using System.Security.Claims;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Plugins;
using Inventory.Server.Services;
using Microsoft.AspNetCore.Authorization;

namespace Inventory.Server.Plugins;

/// <summary>
/// Führt Aktionen der Plugins aus, für Oberfläche, API und MCP gleich: Rolle, Operator-Bereich, Protokoll, Fehlerbehandlung.
/// </summary>
public sealed class PluginActions(PluginInstances instances, IAuthorizationService authorization, ComputerScope scope,
    ComputerService computers, AuditService audit, ILogger<PluginActions> logger)
{
    public static string Policy(PluginRole role) => role switch
    {
        PluginRole.Admin => AccessControl.AdminPolicy,
        PluginRole.Operator => AccessControl.OperatorPolicy,
        _ => AccessControl.ViewerPolicy,
    };

    public async Task<bool> AllowedAsync(ClaimsPrincipal user, PluginRole role) =>
        (await authorization.AuthorizeAsync(user, Policy(role))).Succeeded;

    /// <summary>Die Aktionen, die dieser Benutzer ausführen darf.</summary>
    public async Task<List<IComputerAction>> AvailableAsync(ClaimsPrincipal user)
    {
        var result = new List<IComputerAction>();
        foreach (var (action, _) in instances.Actions)
            if (await AllowedAsync(user, action.MinRole))
                result.Add(action);
        return [.. result.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    public string? OwnerName(IComputerAction action) => instances.Actions.FirstOrDefault(a => a.Action == action).Owner?.DisplayName;

    /// <exception cref="KeyNotFoundException">Keine solche Aktion.</exception>
    /// <exception cref="UnauthorizedAccessException">Rolle zu niedrig oder Rechner außerhalb des Operator-Bereichs.</exception>
    public async Task<PluginActionResult> RunAsync(string actionId, IReadOnlyList<int> computerIds, ClaimsPrincipal user, AuditActor actor, CancellationToken ct = default)
    {
        var action = instances.Actions.Select(a => a.Action).FirstOrDefault(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No plugin action '{actionId}'.");
        if (!await AllowedAsync(user, action.MinRole))
            throw new UnauthorizedAccessException($"The action '{action.Title}' needs the {action.MinRole} role.");
        var ids = computerIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new ArgumentException("No computers given.");
        var outside = await scope.OutsideAsync(user, ids, ct);
        if (outside.Count > 0)
            throw new UnauthorizedAccessException($"Outside the operator's area (Inventory:OperatorScopes): computer IDs {string.Join(", ", outside)}.");

        var all = await computers.ListAsync(ct);
        var names = all.Where(c => ids.Contains(c.Id)).Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        PluginActionResult result;
        try
        {
            result = await action.RunAsync(ids, new PluginActionContext(actor.User), ct);
        }
        catch (OperationCanceledException)
        {
            // Bricht der Aufrufer ab, kann die Aktion schon etwas geändert haben: das gehört ins Protokoll.
            await audit.LogAsync(actor, $"Plugin action: {action.Title}", $"{string.Join(", ", names)} – cancelled", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plugin action {Action} failed", action.Id);
            result = PluginActionResult.Failed(ex.Message);
        }
        await audit.LogAsync(actor, $"Plugin action: {action.Title}",
            $"{string.Join(", ", names)} – {(result.Success ? "" : "failed: ")}{result.Message}", CancellationToken.None);
        return result;
    }
}
