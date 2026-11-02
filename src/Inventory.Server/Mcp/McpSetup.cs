using System.Text.Json;
using System.Text.Json.Serialization;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Inventory.Server.Mcp;

/// <summary>
/// MCP-Server unter /mcp (Streamable HTTP, zustandslos): KI-Assistenten fragen das Inventar ab und handeln als Operator.
/// Angemeldet wird mit einem API-Token; welche Werkzeuge es gibt, bestimmt dessen Rolle. Ausgeschaltet, bis ein
/// Administrator ihn in den Einstellungen einschaltet.
/// </summary>
public static class McpSetup
{
    public const string Path = "/mcp";

    /// <summary>Aufzählungen als Namen, wie in der REST-API; leere Werte fallen weg und sparen dem Modell Zeichen.</summary>
    internal static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="pluginTools">Werkzeugklassen der geladenen Plugins.</param>
    public static void AddInventoryMcp(this IServiceCollection services, IEnumerable<Type>? pluginTools = null)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<McpCaller>();
        // Auch als Helfer der übrigen Werkzeuge (Zielrechner aus Namen oder Sammlung).
        services.AddScoped<PackageTools>();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "inventory", Title = "Inventory", Version = AppVersion.Current };
                options.ServerInstructions = "Inventory of Windows computers, software, updates, network devices and IP subnets, plus software deployment. "
                    + "Computers, packages, collections and subnets are accepted by name or id. Lists are capped; narrow them with the filter parameters. "
                    + "Write actions (scan, wake, deploy, reserve) need an Operator token and are logged in the audit log.";
            })
            .WithHttpTransport(options => options.Stateless = true)
            .AddAuthorizationFilters()
            .WithTools<InventoryTools>(Json)
            .WithTools<NetworkTools>(Json)
            .WithTools<OperatorTools>(Json)
            .WithTools<PackageTools>(Json)
            .WithTools<PluginTools>(Json)
            .WithTools(pluginTools ?? [], Json);
    }

    /// <summary>
    /// Vor der Autorisierung: ausgeschaltet = 404; mit Anmeldung nur API-Tokens (Cookies und Windows-Anmeldung würden
    /// sonst eine Anmeldeseite statt eines Statuscodes liefern, und viele MCP-Clients können kein Negotiate).
    /// </summary>
    public static void UseInventoryMcpGate(this WebApplication app, InventoryOptions options) =>
        app.UseWhen(context => context.Request.Path.StartsWithSegments(Path), branch => branch.Use(async (context, next) =>
        {
            if (!await EnabledAsync(context.RequestServices.GetRequiredService<SettingsService>(), context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (options.AuthenticationEnabled && context.User.FindFirst(AccessControl.AccessClaim)?.Value != "api-token")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context);
        }));

    public static void MapInventoryMcp(this WebApplication app) =>
        app.MapMcp(Path).RequireAuthorization(AccessControl.ViewerPolicy).ExcludeFromDescription();

    /// <summary>Die erlaubten internen Download-Hosts aus der Einstellung (Zeilen, Komma oder Leerzeichen getrennt).</summary>
    public static IReadOnlyList<string> DownloadHosts(string? setting) =>
        (setting ?? "").Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static async Task<bool> EnabledAsync(SettingsService settings, CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.McpEnabled, ct) == bool.TrueString;
}
