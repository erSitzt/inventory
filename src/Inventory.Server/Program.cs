using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Agent;
using Inventory.Remoting.Smb;
using Inventory.Remoting.WinRm;
using Inventory.Server;
using Inventory.Server.Api;
using Inventory.Server.Components;
using Inventory.Server.Services;
using System.Globalization;
using Inventory.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Scalar.AspNetCore;

// Diagnose: prüft die Signatur einer Datei so, wie es die Selbstaktualisierung tut.
if (args is ["--verify-signature", var signedFile, ..] && OperatingSystem.IsWindows())
{
    var problem = SelfUpdate.VerifySignature(signedFile, args.Length > 2 ? args[2] : null, SelfUpdate.TrustedThumbprints(AppContext.BaseDirectory, null));
    Console.WriteLine(problem ?? "Signature valid.");
    return problem is null ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);

// Einstellungen des Betreibers liegen außerhalb des Programmordners, damit Updates sie nie überschreiben.
// Umgebungsvariablen und Befehlszeile behalten Vorrang.
if (OperatingSystem.IsWindows())
{
    builder.Configuration
        .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Inventory", "appsettings.json"), optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .AddCommandLine(args);
}

// Unter Windows als Dienst lauffähig; unter Linux/WSL und in der Konsole ohne Wirkung.
builder.Host.UseWindowsService(options => options.ServiceName = "Inventory");

builder.Services.Configure<InventoryOptions>(builder.Configuration.GetSection(InventoryOptions.SectionName));
var options = builder.Configuration.GetSection(InventoryOptions.SectionName).Get<InventoryOptions>() ?? new InventoryOptions();
var dataDirectory = options.ResolveDataDirectory();
Directory.CreateDirectory(dataDirectory);

builder.Services.AddDbContextFactory<InventoryDbContext>(db =>
    db.UseSqlite($"Data Source={Path.Combine(dataDirectory, "inventory.db")}"));

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Inventory")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton(TimeProvider.System);
if (options.ResolveSimulate())
{
    builder.Services.AddSingleton<IHostProbe, SimulatedHostProbe>();
    builder.Services.AddSingleton<IRemoteTransport>(new SimulatedTransport(TransportKind.SmbService));
    builder.Services.AddSingleton<IRemoteTransport>(new SimulatedTransport(TransportKind.WinRm));
    builder.Services.AddSingleton<IDirectorySource, SimulatedDirectorySource>();
}
else
{
    builder.Services.AddSingleton<IHostProbe, TcpHostProbe>();
    builder.Services.AddSingleton<IDirectorySource, LdapDirectorySource>();
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton<IRemoteTransport, SmbServiceTransport>();
        builder.Services.AddSingleton<IRemoteTransport, WinRmTransport>();
    }
}
// Der Agent verbindet sich von sich aus; das funktioniert unabhängig vom Betriebssystem des Servers und auch in der Simulation.
builder.Services.AddSingleton(new RunnerPackage(Path.Combine(AppContext.BaseDirectory, "runner")));
builder.Services.AddSingleton<AgentHub>();
builder.Services.AddSingleton<IRemoteTransport, AgentTransport>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<RemoteJobService>();
builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddSingleton<ScanService>();
builder.Services.AddSingleton<CollectionService>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton(new PackageRepository(Path.Combine(dataDirectory, "repository")));
builder.Services.AddSingleton<PackageService>();
builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<AdSyncService>();
builder.Services.AddSingleton<HeartbeatService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<SignInLog>();
builder.Services.AddSingleton(sp => new BackupService(sp.GetRequiredService<IDbContextFactory<InventoryDbContext>>(), Path.Combine(dataDirectory, "backups"), TimeProvider.System));
builder.Services.AddScoped<Auditor>();
builder.Services.AddScoped<Access>();
builder.Services.AddSingleton<IReleaseSource>(new GitHubReleaseSource(new HttpClient { Timeout = TimeSpan.FromMinutes(15) }));
var trustedSigners = SelfUpdate.TrustedThumbprints(AppContext.BaseDirectory, options.Update.TrustedThumbprints);
builder.Services.AddSingleton(new UpdateOptions
{
    Repository = options.Update.Repository,
    Enabled = options.Update.Enabled,
    CurrentVersion = AppVersion.Current,
    UpdatesDirectory = Path.Combine(dataDirectory, "updates"),
    CannotInstallReason = SelfUpdate.CannotInstallReason(),
    Launch = SelfUpdate.Launch,
    // Bringt die Version Zertifikate mit (oder ist ein Herausgeber konfiguriert), wird ohne gültige Signatur nichts eingespielt.
    VerifySignature = options.Update.AllowUnsigned || (trustedSigners.Count == 0 && string.IsNullOrWhiteSpace(options.Update.RequiredPublisher))
        ? null
        : setup => OperatingSystem.IsWindows() ? SelfUpdate.VerifySignature(setup, options.Update.RequiredPublisher, trustedSigners) : "Signature verification is only available on Windows.",
});
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton(new DefenderClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
builder.Services.AddSingleton<ICatalogSource>(new HttpCatalogSource(new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(30) }));
builder.Services.AddSingleton(sp => new CatalogService(sp.GetRequiredService<IDbContextFactory<InventoryDbContext>>(), sp.GetRequiredService<ICatalogSource>(),
    sp.GetRequiredService<PackageService>(), sp.GetRequiredService<CollectionService>(), sp.GetRequiredService<AuditService>(), TimeProvider.System, options.Catalog.Url, options.Catalog.IndexUrl, options.Catalog.VersionsUrl));
builder.Services.AddHostedService<SchedulerWorker>();

var windowsAuthentication = options.ResolveWindowsAuthentication();
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.AddInventoryAccess(options);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<ComputerService>();
builder.Services.AddSingleton<CredentialService>();
builder.Services.AddSingleton<IMailSender, MailKitSender>();
builder.Services.AddSingleton<IWebhookSender>(new HttpWebhookSender(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<AlertQueue>();
builder.Services.AddSingleton<IAlertSink>(sp => sp.GetRequiredService<AlertQueue>());
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ExampleData>();

// Enums in der REST-API als Namen statt Zahlen.
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Sprache: Übersetzungen aus Inventory.Core/Resources/SharedResource.<kultur>.resx (IStringLocalizer); Browser-Sprache oder
// Cookie (Umschalter in der Kopfleiste), sonst die konfigurierte Standardsprache.
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
var languages = Languages.Available();
var defaultLanguage = languages.Any(l => l.Code.Equals(options.Language, StringComparison.OrdinalIgnoreCase)) ? options.Language!.ToLowerInvariant() : Languages.Source;
var supportedCultures = languages.Select(l => new CultureInfo(l.Code)).ToList();
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(defaultLanguage);
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.SetDefaultCulture(defaultLanguage);
    o.SupportedCultures = supportedCultures;
    o.SupportedUICultures = supportedCultures;
    o.FallBackToParentCultures = true;
    o.FallBackToParentUICultures = true;
});
builder.Services.AddSingleton<Display>();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Inventory API";
    document.Info.Version = AppVersion.Current;
    document.Info.Description = "REST interface of Inventory. Authenticate with an API token (Authorization: Bearer inv_…) created in the UI under " +
                                "\"API tokens\"; with Windows sign-in enabled, Negotiate works as well. Every call that executes something (scan, command, deployment) is logged.";
    document.Components ??= new();
    document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["ApiToken"] = new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "inv_…",
        Description = "API token from the UI (API tokens).",
    };
    document.Security = [new Microsoft.OpenApi.OpenApiSecurityRequirement { [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("ApiToken", document)] = [] }];
    return Task.CompletedTask;
}));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices(config => config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight);

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<InventoryDbContext>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();
await app.Services.GetRequiredService<InventoryStore>().BackfillPendingUpdatesAsync();
await app.Services.GetRequiredService<ExampleData>().SeedSchedulesAsync();
await app.Services.GetRequiredService<DeploymentService>().MarkInterruptedAsync();
await app.Services.GetRequiredService<UpdateService>().ReportPreviousAsync(startup: true);

app.Logger.LogInformation("Inventory {Version}. Data directory: {DataDirectory}, simulation: {Simulate}, sign-in: {Authentication}",
    AppVersion.Current, dataDirectory, options.ResolveSimulate(), string.Join(" + ", new[]
    {
        windowsAuthentication ? $"Windows ({string.Join(", ", options.DescribeAllowedGroups())})" : null,
        options.OidcEnabled ? $"OIDC ({options.Oidc.ResolveAuthority()})" : null,
    }.Where(m => m is not null).DefaultIfEmpty("none")));
if (!options.AuthenticationEnabled && !options.ResolveSimulate())
    app.Logger.LogWarning("Sign-in is disabled: anyone who can reach this address can run commands on the target computers with the stored credentials.");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Vor den Statusseiten: deren erneute Ausführung der Anfrage würde den mehrstufigen Negotiate-Handshake zerstören.
app.UseRequestLocalization();
if (options.AuthenticationEnabled)
    app.UseAuthentication();
if (options.ResolveWindowsAuthentication())
    app.UseMiddleware<WindowsSignInAuditMiddleware>();
app.UseAuthorization();
// Nur für die Oberfläche: API-Aufrufer (Skripte, Agenten) brauchen den echten Statuscode. Der Agent meldet sich
// z. B. bei 401 neu an; die Fehlerseite würde daraus bei POST-Anfragen ein 400 machen.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseAntiforgery();

app.MapStaticAssets();
app.MapComputerEndpoints();
app.MapPackageEndpoints();
app.MapReportEndpoints();
app.MapManagementEndpoints();
app.MapAgentEndpoints();
// Dokumentation unter /api/docs, Beschreibung unter /api/openapi.json; gleiche Anmeldung wie die übrige API.
app.MapOpenApi("/api/openapi.json");
app.MapScalarApiReference("/api/docs", o => o.WithTitle("Inventory API").DisableDefaultFonts().WithOpenApiRoutePattern("/api/openapi.json").WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl));
app.MapInventoryLogin(options);
// Sprachumschalter: Cookie setzen und zurück zur Seite, von der der Klick kam.
app.MapGet("/language/{code}", (string code, HttpContext http) =>
{
    if (languages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
    {
        http.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(code.ToLowerInvariant())),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
    }
    var back = http.Request.Headers.Referer.ToString();
    return Results.LocalRedirect(Uri.TryCreate(back, UriKind.Absolute, out var uri) && uri.Host == http.Request.Host.Host ? uri.PathAndQuery : "/");
}).AllowAnonymous().ExcludeFromDescription();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
