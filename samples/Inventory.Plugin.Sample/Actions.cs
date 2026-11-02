using System.ComponentModel;
using Inventory.Data.Services;
using Inventory.Plugins;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Inventory.Plugin.Sample;

/// <summary>Harmlose Aktion: nennt die IP-Adressen der gewählten Rechner, ändert nichts.</summary>
public sealed class ListAddressesAction(ComputerService computers) : IComputerAction
{
    public string Id => "inventory.sample.addresses";
    public string Title => "Show IP addresses (sample)";
    public PluginRole MinRole => PluginRole.Viewer;

    public async Task<PluginActionResult> RunAsync(IReadOnlyList<int> computerIds, PluginActionContext context, CancellationToken ct)
    {
        var list = (await computers.ListAsync(ct)).Where(c => computerIds.Contains(c.Id)).OrderBy(c => c.Name);
        return PluginActionResult.Ok(string.Join(", ", list.Select(c => $"{c.Name} = {c.IpAddress ?? "?"}")));
    }
}

/// <summary>Zählt jede Stunde die Rechner und schreibt die Zahl in den Datenordner des Plugins.</summary>
public sealed class CountJob(ComputerService computers, SampleState state) : IPeriodicJob
{
    public string Name => "count";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken ct)
    {
        state.LastCount = (await computers.ListAsync(ct)).Count;
        await File.WriteAllTextAsync(state.File, $"{DateTime.UtcNow:O} {state.LastCount}", ct);
    }
}

[McpServerToolType, Authorize(Policy = PluginPolicies.Viewer)]
public sealed class SampleTools(ComputerService computers)
{
    [McpServerTool(Name = "sample_computer_count", Title = "Count computers (sample)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Sample plugin tool: the number of inventoried computers, and how many are online.")]
    public async Task<object> Count(CancellationToken ct = default)
    {
        var list = await computers.ListAsync(ct);
        return new { total = list.Count, online = list.Count(c => c.IsOnline) };
    }
}
