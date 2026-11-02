using Inventory.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: InventoryPlugin(typeof(Inventory.Plugin.Sample.SamplePlugin), ApiVersion = "1.1")]

namespace Inventory.Plugin.Sample;

/// <summary>Beispiel für jeden Erweiterungspunkt: Seite, Rechner-Reiter, Einstellungen, Aktion, Auftrag und MCP-Werkzeug.</summary>
public sealed class SamplePlugin : IInventoryPlugin
{
    public const string ExecutionPolicy = "inventory.sample.executionPolicy";
    public const string NameLength = "inventory.sample.nameLength";

    public string Id => "inventory.sample";
    public string Name => "Sample plugin";
    public string? Description => "Shows every extension point: a page, a computer tab, a settings section, an action, a job, an MCP tool and two inventory fields.";

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton(new SampleState(context.DataDirectory));
        services.AddSingleton(new PluginNavItem("Sample", "sample"));
        services.AddSingleton(new PluginComputerTab("Sample", typeof(SampleTab)));
        services.AddSingleton(new PluginSettingsSection("Sample plugin", typeof(SampleSettings)));
        services.AddSingleton<IComputerAction, ListAddressesAction>();
        services.AddSingleton<IPeriodicJob, CountJob>();
        services.AddSingleton(new PluginMcpTools(typeof(SampleTools)));

        // Eigene Inventarfelder: eines kommt von einem Skript auf dem Rechner, das andere aus dem Auftrag oben (wie aus einem anderen System).
        services.AddSingleton(new CustomField(ExecutionPolicy, "PowerShell execution policy"));
        services.AddSingleton(new CustomField(NameLength, "Length of the computer name", CustomFieldType.Number));
        services.AddSingleton(new InventoryScript("execution-policy", """
            # simulate: {"executionPolicy":"RemoteSigned"}
            @{ executionPolicy = [string](Get-ExecutionPolicy -Scope LocalMachine) } | ConvertTo-Json -Compress
            """));
    }

    public Task StartAsync(IServiceProvider services, CancellationToken ct)
    {
        services.GetRequiredService<SampleState>().Started = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}

/// <summary>Was das Beispiel sich merkt: Startzeit und das Ergebnis des letzten Auftrags (auch als Datei im eigenen Ordner).</summary>
public sealed class SampleState(string dataDirectory)
{
    public DateTime? Started { get; set; }
    public int? LastCount { get; set; }
    public string File { get; } = Path.Combine(dataDirectory, "last-count.txt");
}
