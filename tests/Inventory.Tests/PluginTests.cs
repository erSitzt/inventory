using System.Net.Http.Json;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Plugins;
using Inventory.Server.Mcp;
using Inventory.Server.Plugins;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

[assembly: InventoryPlugin(typeof(Inventory.Tests.ThrowingPlugin))]

namespace Inventory.Tests;

/// <summary>Dient als fehlerhaftes Plugin: wirft, nachdem es schon etwas angemeldet hat.</summary>
public sealed class ThrowingPlugin : IInventoryPlugin
{
    public string Id => "tests.throwing";
    public string Name => "Throwing";

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton(new PluginNavItem("Half", "half"));
        throw new InvalidOperationException("broken on purpose");
    }
}

/// <summary>Laden der Plugins: eingeschaltet, aus, geändert, kaputt, inkompatibel; geteilte Typen mit dem Server.</summary>
public class PluginLoaderTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"inventory-plugins-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Geladene Assemblies bleiben unter Windows gesperrt; ein Temp-Ordner darf keinen Test scheitern lassen.
        }
    }

    /// <summary>Kopiert das gebaute Beispiel-Plugin in den Plugin-Ordner, wie ein Administrator es täte.</summary>
    internal static string InstallSample(string dataDirectory, bool enable = true)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inventory.sln")))
            dir = dir.Parent;
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var output = Path.Combine(dir!.FullName, "samples", "Inventory.Plugin.Sample", "bin", configuration, "net10.0");
        var target = Path.Combine(dataDirectory, PluginLoader.FolderName, "Inventory.Plugin.Sample");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(output))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        if (enable)
            PluginLoader.SetEnabled(Path.Combine(dataDirectory, PluginLoader.FolderName), "Inventory.Plugin.Sample", PluginLoader.Hash(target));
        return target;
    }

    private PluginCatalog Load(IServiceCollection services) =>
        PluginLoader.Load(_data, services, new ConfigurationBuilder().Build(), "1.2.3");

    [Fact]
    public void An_enabled_plugin_is_loaded_and_shares_the_host_types()
    {
        InstallSample(_data);
        var services = new ServiceCollection();
        var catalog = Load(services);

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal((PluginState.Loaded, "inventory.sample", "1.0"), (plugin.State, plugin.Id, plugin.ApiVersion));
        Assert.Contains("1 action", plugin.Contributions);
        Assert.Single(catalog.McpToolTypes);
        Assert.True(Directory.Exists(Path.Combine(_data, "plugins-data", "inventory.sample")));

        // Dieselben Typen wie im Server: sonst könnte der Router die Seiten nicht rendern und DI die Beiträge nicht finden.
        var tab = (PluginComputerTab)Assert.Single(services, d => d.ServiceType == typeof(PluginComputerTab)).ImplementationInstance!;
        Assert.True(tab.Component.IsAssignableTo(typeof(ComponentBase)));
        Assert.Same(catalog.Owner(tab.Component), plugin);
        Assert.Contains(services, d => d.ServiceType == typeof(IComputerAction));
    }

    [Fact]
    public void A_plugin_that_is_off_or_changed_is_not_loaded()
    {
        var folder = InstallSample(_data, enable: false);
        var services = new ServiceCollection();
        Assert.Equal(PluginState.Disabled, Assert.Single(Load(services).Plugins).State);
        Assert.Empty(services);

        PluginLoader.SetEnabled(Path.Combine(_data, PluginLoader.FolderName), "Inventory.Plugin.Sample", PluginLoader.Hash(folder));
        // Eine ausgetauschte Abhängigkeit zählt wie eine geänderte Haupt-DLL.
        File.WriteAllBytes(Path.Combine(folder, "Extra.dll"), [1, 2, 3]);
        var changed = Assert.Single(Load(services).Plugins);
        Assert.Equal(PluginState.Changed, changed.State);
        Assert.Empty(services);
    }

    [Fact]
    public void Broken_plugins_are_reported_and_leave_nothing_behind()
    {
        var missing = Path.Combine(_data, PluginLoader.FolderName, "NoDll");
        Directory.CreateDirectory(missing);
        var notAPlugin = Path.Combine(_data, PluginLoader.FolderName, "NotAPlugin");
        Directory.CreateDirectory(notAPlugin);
        File.Copy(typeof(Inventory.Contracts.JobManifest).Assembly.Location, Path.Combine(notAPlugin, "NotAPlugin.dll"));
        PluginLoader.SetEnabled(Path.Combine(_data, PluginLoader.FolderName), "NotAPlugin", PluginLoader.Hash(notAPlugin));

        var services = new ServiceCollection();
        var catalog = Load(services);
        Assert.Contains("NoDll.dll not found", catalog.Plugins.Single(p => p.Folder == "NoDll").Error);
        Assert.Contains("InventoryPlugin", catalog.Plugins.Single(p => p.Folder == "NotAPlugin").Error);
        Assert.Empty(services);

        // Wirft ConfigureServices, landet auch das schon Angemeldete nicht im Server.
        var info = new PluginInfo { Folder = "Throwing", MainFile = "", Sha256 = "x", State = PluginState.Loaded };
        PluginLoader.Initialize(info, typeof(ThrowingPlugin).Assembly, new PluginCatalog(_data, [info]), services, new ConfigurationBuilder().Build(), _data, "1.2.3");
        Assert.Equal((PluginState.Failed, "broken on purpose"), (info.State, info.Error));
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("1.0", null)]
    [InlineData("1.1", "needs API 1.1")]
    [InlineData("2.0", "built for API 2.x")]
    [InlineData("0.9", "built for API 0.x")]
    [InlineData("one", "invalid API version")]
    public void The_api_version_decides_compatibility(string version, string? reason)
    {
        var result = PluginApi.Incompatibility(version);
        if (reason is null)
            Assert.Null(result);
        else
            Assert.Contains(reason, result);
    }
}

/// <summary>Der echte Server mit eingeschaltetem Beispiel-Plugin (Simulationsmodus, ohne Anmeldung).</summary>
public sealed class PluginHostTests : IClassFixture<PluginHostTests.App>
{
    private readonly App _app;

    public PluginHostTests(App app) => _app = app;

    public sealed class App : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"inventory-pluginhost-{Guid.NewGuid():N}");

        public App() => PluginLoaderTests.InstallSample(DataDirectory);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Inventory:DataDirectory", DataDirectory);
            builder.UseSetting("Inventory:Simulate", "true");
            builder.UseSetting("Inventory:Authentication", "None");
            builder.UseSetting("Inventory:Update:Enabled", "false");
            builder.UseEnvironment("Development");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Die geladene Plugin-DLL bleibt unter Windows gesperrt.
            }
        }
    }

    [Fact]
    public async Task The_plugin_page_is_routed_by_the_server()
    {
        using var http = _app.CreateClient();
        var html = await http.GetStringAsync("/sample");
        Assert.Contains("This page comes from a plugin", html);
    }

    [Fact]
    public async Task A_plugin_action_runs_through_the_api_and_is_logged()
    {
        var computers = await _app.Services.GetRequiredService<ComputerService>().ImportAsync("PLUGIN-PC-01");
        using var http = _app.CreateClient();
        var actions = await http.GetFromJsonAsync<List<Inventory.Server.Api.PluginActionDto>>("/api/plugins/actions");
        Assert.Contains(actions!, a => a.Id == "inventory.sample.addresses");

        var response = await http.PostAsJsonAsync("/api/plugins/actions/inventory.sample.addresses", new { computerIds = new[] { computers[0].Id } });
        var result = await response.Content.ReadFromJsonAsync<Inventory.Server.Api.PluginActionResultDto>();
        Assert.True(result!.Success);
        Assert.StartsWith("PLUGIN-PC-01 = ", result.Message);
        Assert.Single(await _app.Services.GetRequiredService<AuditService>().ListAsync(20, filter: new AuditFilter(Action: "Plugin action: Show IP addresses (sample)")));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/plugins/actions/nope", new { computerIds = new[] { 1 } })).StatusCode);
    }

    [Fact]
    public async Task Plugin_tools_are_offered_over_mcp_and_the_job_runs()
    {
        await _app.Services.GetRequiredService<SettingsService>().SetAsync(SettingKeys.McpEnabled, bool.TrueString);
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_app.Server.BaseAddress, McpSetup.Path) }, _app.CreateClient(), ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("sample_computer_count", tools);
        Assert.Contains("run_plugin_action", tools);

        // Der Auftrag läuft gleich nach dem Start einmal und schreibt in den Datenordner des Plugins.
        var file = Path.Combine(_app.DataDirectory, "plugins-data", "inventory.sample", "last-count.txt");
        for (var i = 0; i < 100 && !File.Exists(file); i++)
            await Task.Delay(100);
        Assert.True(File.Exists(file));
        var catalog = _app.Services.GetRequiredService<PluginCatalog>();
        Assert.Equal("count", Assert.Single(Assert.Single(catalog.Loaded).Jobs).Name);
    }
}
