# Inventory.Plugins

The plugin interface of [Inventory](https://github.com/erSitzt/inventory). A plugin is a .NET 10 class library (a Razor
class library if it brings pages or tabs) that the Inventory server loads at start.

```csharp
[assembly: InventoryPlugin(typeof(MyPlugin), ApiVersion = "1.0")]

public sealed class MyPlugin : IInventoryPlugin
{
    public string Id => "contoso.example";
    public string Name => "Example";

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton(new PluginNavItem("Example", "contoso/example"));
        services.AddSingleton(new PluginComputerTab("Example", typeof(ExampleTab)));
        services.AddSingleton<IComputerAction, ExampleAction>();
        services.AddSingleton<IPeriodicJob, ExampleJob>();
    }
}
```

Install it by copying the build output into `C:\ProgramData\Inventory\plugins\<AssemblyName>\` (the main DLL must be
named like the folder), then switch it on under *Plugins* in the web interface and restart the service. See the
"Plugins" section of the Inventory README for details, the compatibility rules and the security notes.
