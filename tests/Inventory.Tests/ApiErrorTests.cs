using System.Net;
using System.Text.Json;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Tests;

/// <summary>
/// Ein unerwarteter Fehler bei einer API- oder MCP-Anfrage: Problem Details mit Statuscode statt der HTML-Fehlerseite.
/// Den Fehler wirft eine Middleware hinter der Pipeline des Servers, so wie eine Ausnahme bei Anmeldung, MCP-Schalter oder
/// Antwortversand, die keinen Endpunkt mehr erreicht.
/// </summary>
public sealed class ApiErrorTests : IClassFixture<ApiErrorTests.App>
{
    private readonly App _app;

    public ApiErrorTests(App app) => _app = app;

    public sealed class App : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"inventory-apierrors-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Inventory:DataDirectory", DataDirectory);
            builder.UseSetting("Inventory:Simulate", "true");
            // Anmeldung eingeschaltet wie im Betrieb; API-Tokens gehen ohne Anbieter (siehe McpTests).
            builder.UseSetting("Inventory:Authentication", "Oidc");
            builder.UseSetting("Inventory:Oidc:Authority", "https://login.example.invalid");
            builder.UseSetting("Inventory:Oidc:ClientId", "inventory-tests");
            builder.UseSetting("Inventory:Oidc:AllowedGroups:0", "inventory-admins");
            builder.UseSetting("Inventory:Update:Enabled", "false");
            builder.UseSetting("Inventory:Https:Enabled", "false");
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, FailingTail>());
        }

        public async Task<string> TokenAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync("api-errors", "test", null, role: AccessRole.Viewer);
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
                // Ein Temp-Verzeichnis darf keinen Test scheitern lassen.
            }
        }
    }

    /// <summary>Hinter der ganzen Pipeline: Anfragen, die keinen Endpunkt treffen, landen hier und scheitern nach Wunsch.</summary>
    private sealed class FailingTail : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(context => context.Request.Path.Value!.EndsWith("/fail-busy", StringComparison.Ordinal)
                ? throw new SqliteException("database is locked", 5)
                : throw new InvalidOperationException("Something broke."));
        };
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? accept = null)
    {
        using var http = _app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", await _app.TokenAsync());
        if (accept is not null)
            request.Headers.Accept.ParseAdd(accept);
        return await http.SendAsync(request);
    }

    [Theory]
    [InlineData("/api/fail")]
    [InlineData("/mcp/fail")]
    public async Task An_unexpected_error_is_problem_details_and_never_html(string path)
    {
        await _app.Services.GetRequiredService<SettingsService>().SetAsync(SettingKeys.McpEnabled, bool.TrueString);
        // Auch wenn der Aufrufer wie ein Browser HTML bevorzugt.
        using var response = await SendAsync(path, "text/html");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        var requestId = problem.GetProperty("requestId").GetString();
        Assert.False(string.IsNullOrEmpty(requestId));
        Assert.Contains(requestId!, problem.GetProperty("detail").GetString());
        // Interna wie die Meldung der Ausnahme bleiben im Protokoll des Servers.
        Assert.DoesNotContain("Something broke", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_busy_database_is_a_503_with_retry_after()
    {
        using var response = await SendAsync("/api/fail-busy");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(ApiErrors.RetryAfterSeconds), response.Headers.RetryAfter?.Delta);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("database is busy", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public void Busy_and_locked_are_found_inside_other_exceptions()
    {
        Assert.True(ApiErrors.IsDatabaseBusy(new InvalidOperationException("save failed", new SqliteException("database is locked", 5))));
        Assert.True(ApiErrors.IsDatabaseBusy(new SqliteException("database table is locked", 6)));
        Assert.False(ApiErrors.IsDatabaseBusy(new SqliteException("constraint failed", 19)));
        Assert.False(ApiErrors.IsDatabaseBusy(null));
    }
}
