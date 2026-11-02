using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Plugins;

/// <summary>Version der Plugin-Schnittstelle. Gleiche Hauptversion, Nebenversion höchstens die des Servers: das Plugin passt.</summary>
public static class PluginApi
{
    public static readonly Version Version = new(1, 0);

    /// <summary>null, wenn ein Plugin mit dieser Schnittstellenversion geladen werden kann; sonst der Grund.</summary>
    public static string? Incompatibility(string? apiVersion)
    {
        if (!Version.TryParse(apiVersion, out var wanted))
            return $"invalid API version '{apiVersion}'";
        if (wanted.Major != Version.Major)
            return $"built for API {wanted.Major}.x, this server provides {Version.Major}.{Version.Minor}";
        return wanted.Minor > Version.Minor ? $"needs API {wanted.Major}.{wanted.Minor}, this server provides {Version.Major}.{Version.Minor}" : null;
    }
}

/// <summary>Kennzeichnet die Plugin-Assembly und ihren Einstiegstyp.</summary>
/// <param name="entryType">Klasse mit parameterlosem Konstruktor, die <see cref="IInventoryPlugin"/> umsetzt.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class InventoryPluginAttribute(Type entryType) : Attribute
{
    public Type EntryType { get; } = entryType;

    /// <summary>Version der Schnittstelle, gegen die das Plugin gebaut ist (siehe <see cref="PluginApi.Version"/>).</summary>
    public string ApiVersion { get; init; } = "1.0";
}

/// <summary>
/// Einstieg eines Plugins. Der Server ruft <see cref="ConfigureServices"/> beim Start einmal auf; dort meldet das Plugin
/// seine Dienste und Beiträge an (<see cref="PluginNavItem"/>, <see cref="PluginComputerTab"/>, <see cref="IComputerAction"/> …).
/// </summary>
public interface IInventoryPlugin
{
    /// <summary>Eindeutig und unveränderlich, z. B. "contoso.tickets"; Schlüssel für Einstellungen und Datenordner.</summary>
    string Id { get; }

    string Name { get; }

    string? Description => null;

    void ConfigureServices(IServiceCollection services, PluginContext context);

    /// <summary>Nach dem Start des Servers (Datenbank migriert); für einmalige Vorbereitung.</summary>
    Task StartAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}

/// <param name="DataDirectory">Eigener Ordner des Plugins für Dateien; besteht bereits.</param>
/// <param name="Configuration">Abschnitt <c>Plugins:&lt;Id&gt;</c> der Server-Konfiguration (appsettings.json).</param>
/// <param name="HostVersion">Version des Servers.</param>
/// <remarks>Protokolliert wird wie sonst im Server über <c>ILogger&lt;T&gt;</c> in den eigenen Diensten.</remarks>
public sealed record PluginContext(string Id, string DataDirectory, IConfiguration Configuration, string HostVersion);

/// <summary>Rollen wie im Server; ein Beitrag ist für diese Rolle und alle höheren sichtbar.</summary>
public enum PluginRole
{
    Viewer = 1,
    Operator = 2,
    Admin = 3,
}

/// <summary>Namen der Berechtigungsrichtlinien, z. B. für <c>[Authorize(Policy = PluginPolicies.Operator)]</c> an MCP-Werkzeugen.</summary>
public static class PluginPolicies
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";
}
