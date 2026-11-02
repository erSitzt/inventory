using Inventory.Plugins;

namespace Inventory.Server.Plugins;

/// <summary>
/// Erzeugt Aktionen und Aufträge der Plugins einzeln statt über IEnumerable aus dem Container: wirft der Konstruktor eines
/// Beitrags, fehlt nur dieser (der Fehler steht beim Plugin), und alle anderen Plugins arbeiten weiter.
/// </summary>
public sealed class PluginInstances(IServiceProvider services, PluginCatalog catalog, ILogger<PluginInstances> logger)
{
    private readonly Lazy<List<(IComputerAction Action, PluginInfo Owner)>> _actions = new(() => [.. Create<IComputerAction>(services, catalog, logger, p => p.ActionDescriptors)]);
    private readonly Lazy<List<(IPeriodicJob Job, PluginInfo Owner)>> _jobs = new(() => [.. Create<IPeriodicJob>(services, catalog, logger, p => p.JobDescriptors)]);

    public IReadOnlyList<(IComputerAction Action, PluginInfo Owner)> Actions => _actions.Value;

    public IReadOnlyList<(IPeriodicJob Job, PluginInfo Owner)> Jobs => _jobs.Value;

    private static IEnumerable<(T, PluginInfo)> Create<T>(IServiceProvider services, PluginCatalog catalog, ILogger logger, Func<PluginInfo, List<ServiceDescriptor>> descriptors)
        where T : class
    {
        foreach (var plugin in catalog.Loaded)
        {
            foreach (var descriptor in descriptors(plugin))
            {
                T? instance = null;
                try
                {
                    instance = (descriptor.ImplementationInstance
                        ?? descriptor.ImplementationFactory?.Invoke(services)
                        ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!)) as T;
                }
                catch (Exception ex)
                {
                    var name = descriptor.ImplementationType?.Name ?? typeof(T).Name;
                    logger.LogError(ex, "Plugin {Plugin}: {Type} could not be created", plugin.DisplayName, name);
                    plugin.Error = $"{name} could not be created: {ex.Message}";
                }
                if (instance is not null)
                    yield return (instance, plugin);
            }
        }
    }
}
