using System.ComponentModel;
using Inventory.Server.Plugins;
using Inventory.Server.Services;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Inventory.Server.Mcp;

/// <summary>Aktionen der Plugins über MCP; welche erlaubt sind, bestimmt die Mindestrolle der einzelnen Aktion.</summary>
[McpServerToolType, Authorize(Policy = AccessControl.ViewerPolicy)]
public sealed class PluginTools(McpCaller caller, PluginActions actions)
{
    [McpServerTool(Name = "list_plugin_actions", Title = "Plugin actions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Actions that installed plugins add for computers (e.g. create a ticket, move to an OU), with the role each needs. Run one with run_plugin_action.")]
    public async Task<object> ListPluginActions() =>
        (await actions.AvailableAsync(caller.User)).Select(a => new { a.Id, a.Title, plugin = actions.OwnerName(a), minRole = a.MinRole.ToString(), confirm = a.Confirm });

    [McpServerTool(Name = "run_plugin_action", Title = "Run a plugin action", Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Runs a plugin action for computers and returns its result. What it does depends on the plugin: check list_plugin_actions first, and ask the user before running it.")]
    public async Task<object> RunPluginAction(
        [Description("Action id from list_plugin_actions.")] string action,
        [Description("Computer names or ids.")] string[] computers,
        CancellationToken ct = default)
    {
        var targets = await caller.ComputersAsync(computers, ct);
        try
        {
            var result = await actions.RunAsync(action, [.. targets.Select(c => c.Id)], caller.User, AuditActors.McpActor(caller.User), ct);
            return new { result.Success, result.Message };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            throw new McpException(ex.Message);
        }
    }
}
