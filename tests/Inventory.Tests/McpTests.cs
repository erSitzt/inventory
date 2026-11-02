using System.Net;
using System.Text.Json;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Inventory.Tests;

/// <summary>
/// Der MCP-Server im echten Server (Simulationsmodus, Anmeldung eingeschaltet), angesprochen mit dem MCP-Client
/// des SDK über HTTP: Schalter, Anmeldung nur per API-Token, Werkzeuge je Rolle, Protokolleinträge.
/// </summary>
public sealed class McpTests : IClassFixture<McpTests.App>
{
    private readonly App _app;

    public McpTests(App app) => _app = app;

    public sealed class App : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"inventory-mcp-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Inventory:DataDirectory", DataDirectory);
            builder.UseSetting("Inventory:Simulate", "true");
            // OIDC statt Windows: der Negotiate-Handler läuft nur unter Kestrel. Die Metadaten des Anbieters werden erst
            // bei einer Browser-Anmeldung geladen, die hier nie stattfindet; der Weg über API-Tokens ist derselbe.
            builder.UseSetting("Inventory:Authentication", "Oidc");
            builder.UseSetting("Inventory:Oidc:Authority", "https://login.example.invalid");
            builder.UseSetting("Inventory:Oidc:ClientId", "inventory-tests");
            builder.UseSetting("Inventory:Oidc:AllowedGroups:0", "inventory-admins");
            builder.UseSetting("Inventory:Update:Enabled", "false");
            builder.UseEnvironment("Development");
        }

        public async Task<string> TokenAsync(AccessRole role)
        {
            using var scope = Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync($"mcp-{role}", "test", null, role: role));
        }

        public Task SetEnabledAsync(bool enabled) =>
            Services.GetRequiredService<SettingsService>().SetAsync(SettingKeys.McpEnabled, enabled ? bool.TrueString : null);

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
                // Ein Temp-Verzeichnis darf keinen Test scheitern lassen.
            }
        }
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_app.Server.BaseAddress, McpSetup.Path),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, _app.CreateClient(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private async Task<HttpStatusCode> PostAsync(string? token)
    {
        using var http = _app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, McpSetup.Path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            request.Headers.Authorization = new("Bearer", token);
        return (await http.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task The_endpoint_is_off_by_default_and_then_only_takes_api_tokens()
    {
        var token = await _app.TokenAsync(AccessRole.Viewer);
        await _app.SetEnabledAsync(false);
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(token));

        await _app.SetEnabledAsync(true);
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("inv_0000000000000000000000000000000000000000000000"));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(token));
    }

    [Fact]
    public async Task The_role_of_the_token_decides_which_tools_exist()
    {
        await _app.SetEnabledAsync(true);
        await using var viewer = await ConnectAsync(await _app.TokenAsync(AccessRole.Viewer));
        await using var operatorClient = await ConnectAsync(await _app.TokenAsync(AccessRole.Operator));
        await using var admin = await ConnectAsync(await _app.TokenAsync(AccessRole.Admin));

        var viewerTools = (await viewer.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        var operatorTools = (await operatorClient.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        var adminTools = (await admin.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Contains("search_computers", viewerTools);
        Assert.Contains("list_subnets", viewerTools);
        Assert.DoesNotContain("scan_computers", viewerTools);
        Assert.DoesNotContain("reserve_ips", viewerTools);
        Assert.DoesNotContain("audit_log", viewerTools);
        Assert.True(viewerTools.IsProperSubsetOf(operatorTools));
        // Für Administratoren gibt es keine zusätzlichen Werkzeuge.
        Assert.Equal(operatorTools, adminTools);

        // Auch direkt aufgerufen bleibt ein Operator-Werkzeug dem Betrachter verschlossen.
        await Assert.ThrowsAsync<McpProtocolException>(() => viewer.CallToolAsync("start_ip_scan").AsTask());
    }

    [Fact]
    public async Task An_operator_reserves_addresses_and_the_audit_log_names_mcp()
    {
        await _app.SetEnabledAsync(true);
        var ipAddresses = _app.Services.GetRequiredService<IpAddressService>();
        await ipAddresses.SaveSubnetAsync(null, "Mcp lab", "10.0.40.0/28", null, null, null, null, false, 12);
        await using var client = await ConnectAsync(await _app.TokenAsync(AccessRole.Operator));

        var result = await client.CallToolAsync("reserve_ips", new Dictionary<string, object?> { ["subnet"] = "Mcp lab", ["count"] = 2, ["contiguous"] = true, ["note"] = "web cluster" });
        Assert.NotEqual(true, result.IsError);
        var reserved = JsonDocument.Parse(Text(result)).RootElement.EnumerateArray().Select(e => e.GetProperty("address").GetString()).ToList();
        Assert.Equal(2, reserved.Count);

        var entries = await _app.Services.GetRequiredService<AuditService>().ListAsync(20, filter: new AuditFilter(Action: "IP addresses reserved via MCP"));
        var entry = Assert.Single(entries);
        Assert.Equal((AuditSource.Mcp, AccessRole.Operator), (entry.Source, entry.Role));
        Assert.Contains("web cluster", entry.Details);

        // Unbekannte Namen kommen als lesbarer Werkzeugfehler zurück, nicht als Ausnahme.
        var unknown = await client.CallToolAsync("get_computer", new Dictionary<string, object?> { ["computer"] = "does-not-exist" });
        Assert.True(unknown.IsError);
        Assert.Contains("'does-not-exist' not found", Text(unknown));
    }

    [Fact]
    public async Task Search_results_are_compact_json_with_enum_names()
    {
        await _app.SetEnabledAsync(true);
        await using var client = await ConnectAsync(await _app.TokenAsync(AccessRole.Viewer));
        var result = await client.CallToolAsync("list_subnets");
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(Text(result)).RootElement.ValueKind);

        var devices = await client.CallToolAsync("list_devices", new Dictionary<string, object?> { ["kind"] = "Printer" });
        var root = JsonDocument.Parse(Text(devices)).RootElement;
        Assert.True(root.TryGetProperty("total", out _));
        Assert.All(root.GetProperty("items").EnumerateArray(), d => Assert.Equal("Printer", d.GetProperty("kind").GetString()));
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
