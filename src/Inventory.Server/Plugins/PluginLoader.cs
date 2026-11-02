using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Inventory.Core.Abstractions;
using Inventory.Plugins;

namespace Inventory.Server.Plugins;

/// <summary>
/// Lädt beim Start die eingeschalteten Plugins aus &lt;Daten&gt;/plugins/&lt;Name&gt;/&lt;Name&gt;.dll. Jedes bekommt einen eigenen
/// Ladekontext; was der Server selbst mitbringt (ASP.NET Core, MudBlazor, Inventory.*), kommt immer vom Server, damit Typen
/// wie ComponentBase oder IServiceCollection auf beiden Seiten dieselben sind. Ein fehlerhaftes Plugin legt nur sich selbst still.
/// </summary>
public static class PluginLoader
{
    public const string FolderName = "plugins";
    public const string EnabledFile = "enabled.json";

    /// <summary>Eingeschaltete Plugins: Ordner und Prüfsumme der Dateien zum Zeitpunkt des Einschaltens.</summary>
    public sealed record EnabledPlugin(string Folder, string Sha256);

    public static PluginCatalog Load(string dataDirectory, IServiceCollection services, IConfiguration configuration, string hostVersion)
    {
        var directory = Path.Combine(dataDirectory, FolderName);
        Directory.CreateDirectory(directory);
        var plugins = new List<PluginInfo>();
        var catalog = new PluginCatalog(directory, plugins);
        List<EnabledPlugin> enabled;
        try
        {
            enabled = ReadEnabled(directory);
        }
        catch (InvalidOperationException ex)
        {
            enabled = [];
            catalog.EnabledFileError = ex.Message;
        }
        foreach (var folder in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(folder);
            var main = Path.Combine(folder, name + ".dll");
            if (!File.Exists(main))
            {
                plugins.Add(new PluginInfo { Folder = name, MainFile = main, Sha256 = "", State = PluginState.Failed, Error = $"{name}.dll not found in the folder." });
                continue;
            }
            PluginInfo info;
            try
            {
                info = new PluginInfo
                {
                    Folder = name, MainFile = main, Sha256 = Hash(folder),
                    FileVersion = FileVersionInfo.GetVersionInfo(main).FileVersion, Publisher = Publisher(main),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Eine gesperrte oder unlesbare Datei legt nur dieses Plugin still, nicht den Start des Servers.
                plugins.Add(new PluginInfo { Folder = name, MainFile = main, Sha256 = "", State = PluginState.Failed, Error = $"The plugin folder could not be read: {ex.Message}" });
                continue;
            }
            plugins.Add(info);
            var wanted = enabled.FirstOrDefault(e => e.Folder.Equals(name, StringComparison.OrdinalIgnoreCase));
            info.State = wanted is null ? PluginState.Disabled
                : wanted.Sha256 != info.Sha256 ? PluginState.Changed
                : PluginState.Loaded;
            if (info.State == PluginState.Loaded)
                Activate(info, catalog, services, configuration, dataDirectory, hostVersion);
        }
        return catalog;
    }

    private static void Activate(PluginInfo info, PluginCatalog catalog, IServiceCollection services, IConfiguration configuration, string dataDirectory, string hostVersion)
    {
        Assembly assembly;
        try
        {
            assembly = new PluginLoadContext(info.MainFile).LoadFromAssemblyPath(info.MainFile);
        }
        catch (Exception ex)
        {
            Fail(info, PluginState.Failed, ex.Message);
            return;
        }
        Initialize(info, assembly, catalog, services, configuration, dataDirectory, hostVersion);
    }

    /// <summary>Einstiegstyp finden, Version prüfen, Dienste anmelden; jeder Fehler legt nur dieses Plugin still.</summary>
    /// <param name="entryType">Nur für Tests: anderer Einstiegstyp als der im Attribut.</param>
    internal static void Initialize(PluginInfo info, Assembly assembly, PluginCatalog catalog, IServiceCollection services, IConfiguration configuration, string dataDirectory, string hostVersion,
        Type? entryType = null)
    {
        try
        {
            var attribute = assembly.GetCustomAttribute<InventoryPluginAttribute>();
            if (attribute is null)
            {
                Fail(info, PluginState.Failed, "The assembly has no [assembly: InventoryPlugin(...)] attribute.");
                return;
            }
            info.ApiVersion = attribute.ApiVersion;
            if (PluginApi.Incompatibility(attribute.ApiVersion) is { } reason)
            {
                Fail(info, PluginState.Incompatible, reason);
                return;
            }
            if (Activator.CreateInstance(entryType ?? attribute.EntryType) is not IInventoryPlugin plugin)
            {
                Fail(info, PluginState.Failed, $"{attribute.EntryType.FullName} does not implement IInventoryPlugin.");
                return;
            }
            (info.Id, info.Name, info.Description, info.Assembly) = (plugin.Id, plugin.Name, plugin.Description, assembly);
            if (catalog.Loaded.Any(p => p != info && string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Fail(info, PluginState.Failed, $"Another plugin already uses the id '{plugin.Id}'.");
                return;
            }

            // Erst in eine eigene Sammlung: wirft das Plugin mittendrin, bleibt im Server nichts Halbes zurück.
            var own = new ServiceCollection();
            var data = Path.Combine(dataDirectory, "plugins-data", Sanitize(plugin.Id));
            Directory.CreateDirectory(data);
            plugin.ConfigureServices(own, new PluginContext(plugin.Id, data, configuration.GetSection($"Plugins:{plugin.Id}"), hostVersion));
            foreach (var descriptor in own)
                services.Add(descriptor);
            info.Plugin = plugin;
            Describe(info, catalog, own);
        }
        catch (Exception ex)
        {
            Fail(info, PluginState.Failed, ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message);
        }
    }

    private static void Fail(PluginInfo info, PluginState state, string error)
    {
        (info.State, info.Error, info.Plugin) = (state, error, null);
    }

    /// <summary>Was das Plugin angemeldet hat, für die Übersicht; MCP-Werkzeuge braucht der MCP-Server schon vor dem Bauen.</summary>
    private static void Describe(PluginInfo info, PluginCatalog catalog, IServiceCollection own)
    {
        int Count(Type type) => own.Count(d => d.ServiceType == type);
        void Add(int count, string one, string many)
        {
            if (count > 0)
                info.Contributions.Add(count == 1 ? one : $"{count} {many}");
        }
        Add(Count(typeof(PluginNavItem)), "1 page", "pages");
        Add(Count(typeof(PluginComputerTab)), "1 computer tab", "computer tabs");
        Add(Count(typeof(PluginSettingsSection)), "1 settings section", "settings sections");
        Add(Count(typeof(IComputerAction)), "1 action", "actions");
        Add(Count(typeof(IPeriodicJob)), "1 job", "jobs");
        info.ActionDescriptors.AddRange(own.Where(d => d.ServiceType == typeof(IComputerAction)));
        info.JobDescriptors.AddRange(own.Where(d => d.ServiceType == typeof(IPeriodicJob)));
        var tools = own.Where(d => d.ServiceType == typeof(PluginMcpTools)).Select(d => d.ImplementationInstance).OfType<PluginMcpTools>().ToList();
        Add(tools.Count, "1 MCP tool class", "MCP tool classes");
        catalog.McpToolTypes.AddRange(tools.Select(t => t.ToolType));

        // Eigene Felder müssen mit der Id des Plugins beginnen, damit sich zwei Plugins nie ins Gehege kommen.
        var prefix = info.Id + ".";
        var fields = own.Select(d => d.ImplementationInstance).OfType<CustomField>().ToList();
        foreach (var field in fields)
        {
            if (!field.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || field.Key.Length == prefix.Length)
                info.Error = $"Field '{field.Key}' ignored: its key must start with '{prefix}'.";
            else if (catalog.Fields.Any(f => f.Key.Equals(field.Key, StringComparison.OrdinalIgnoreCase)))
                info.Error = $"Field '{field.Key}' ignored: registered twice.";
            else
                catalog.Fields.Add(new CustomFieldInfo(field.Key, field.Title, (CustomFieldKind)field.Type, info.DisplayName));
        }
        var scripts = own.Select(d => d.ImplementationInstance).OfType<InventoryScript>().ToList();
        catalog.Scripts.AddRange(scripts.Select(s => new CustomScriptInfo($"{info.Id}/{s.Name}", s.Name, s.PowerShell, Math.Clamp(s.TimeoutSeconds, 10, 1800), prefix, info.DisplayName)));
        Add(fields.Count, "1 inventory field", "inventory fields");
        Add(scripts.Count, "1 inventory script", "inventory scripts");
    }

    /// <summary>Nach dem Start: StartAsync jedes geladenen Plugins; ein Fehler legt nur dieses still.</summary>
    public static async Task StartAsync(PluginCatalog catalog, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        foreach (var info in catalog.Loaded.ToList())
        {
            try
            {
                await info.Plugin!.StartAsync(services, ct);
                logger.LogInformation("Plugin {Plugin} {Version} started", info.DisplayName, info.FileVersion);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Plugin {Plugin} failed to start", info.DisplayName);
                info.Error = $"Start failed: {ex.Message}";
            }
        }
        foreach (var info in catalog.Plugins.Where(p => p.State is PluginState.Failed or PluginState.Incompatible))
            logger.LogWarning("Plugin {Plugin} not loaded: {Error}", info.DisplayName, info.Error);
    }

    /// <exception cref="InvalidOperationException">Die Datei ist beschädigt; dann wird nichts überschrieben.</exception>
    public static List<EnabledPlugin> ReadEnabled(string directory)
    {
        var file = Path.Combine(directory, EnabledFile);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<EnabledPlugin>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{EnabledFile} cannot be read ({ex.Message}); fix or delete it.", ex);
        }
    }

    /// <summary>Schaltet ein Plugin für den nächsten Start ein oder aus; schreibt über eine temporäre Datei, damit nie eine halbe Liste übrig bleibt.</summary>
    /// <exception cref="InvalidOperationException">Die vorhandene Liste ist beschädigt.</exception>
    public static void SetEnabled(string directory, string folder, string? sha256)
    {
        var list = ReadEnabled(directory).Where(e => !e.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sha256 is not null)
            list.Add(new EnabledPlugin(folder, sha256));
        var file = Path.Combine(directory, EnabledFile);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// Prüfsumme über Namen und Inhalt aller Dateien des Ordners: auch eine ausgetauschte Abhängigkeit oder eine geänderte
    /// .deps.json (sie bestimmt, was geladen wird) fällt auf.
    /// </summary>
    public static string Hash(string folder)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).ToLowerInvariant()));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string? Publisher(string file)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
#pragma warning disable SYSLIB0057 // Nur der Unterzeichner zur Anzeige; die Vertrauensentscheidung trifft der Administrator.
            using var certificate = X509Certificate.CreateFromSignedFile(file);
            using var full = new X509Certificate2(certificate);
#pragma warning restore SYSLIB0057
            return full.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return null;
        }
    }

    private static string Sanitize(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Ladekontext eines Plugins: Assemblies des Servers haben Vorrang, nur Fehlendes kommt aus dem Plugin-Ordner.</summary>
    private sealed class PluginLoadContext(string mainFile) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainFile))
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainFile);

        protected override Assembly? Load(AssemblyName name)
        {
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
            {
                // Nicht im Server (oder nur in älterer Version): dann die Kopie des Plugins.
            }
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
