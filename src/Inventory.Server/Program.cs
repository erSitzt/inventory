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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

// Diagnose: prüft die Signatur einer Datei so, wie es die Selbstaktualisierung tut.
if (args is ["--verify-signature", var signedFile, var publisher] && OperatingSystem.IsWindows())
{
    var problem = SelfUpdate.VerifySignature(signedFile, publisher);
    Console.WriteLine(problem ?? "Signatur gültig.");
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
builder.Services.AddSingleton(new PackageRepository(Path.Combine(dataDirectory, "repository")));
builder.Services.AddSingleton<PackageService>();
builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<AdSyncService>();
builder.Services.AddSingleton<HeartbeatService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton(sp => new BackupService(sp.GetRequiredService<IDbContextFactory<InventoryDbContext>>(), Path.Combine(dataDirectory, "backups"), TimeProvider.System));
builder.Services.AddScoped<Auditor>();
builder.Services.AddSingleton<IReleaseSource>(new GitHubReleaseSource(new HttpClient { Timeout = TimeSpan.FromMinutes(15) }));
builder.Services.AddSingleton(new UpdateOptions
{
    Repository = options.Update.Repository,
    Enabled = options.Update.Enabled,
    CurrentVersion = AppVersion.Current,
    UpdatesDirectory = Path.Combine(dataDirectory, "updates"),
    CannotInstallReason = SelfUpdate.CannotInstallReason(),
    Launch = SelfUpdate.Launch,
    VerifySignature = string.IsNullOrWhiteSpace(options.Update.RequiredPublisher)
        ? null
        : setup => OperatingSystem.IsWindows() ? SelfUpdate.VerifySignature(setup, options.Update.RequiredPublisher) : "Signaturprüfung gibt es nur unter Windows.",
});
builder.Services.AddSingleton<UpdateService>();
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
builder.Services.AddSingleton<SettingsService>();

// Enums in der REST-API als Namen statt Zahlen.
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices(config => config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight);

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<InventoryDbContext>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();
await app.Services.GetRequiredService<DeploymentService>().MarkInterruptedAsync();
await app.Services.GetRequiredService<UpdateService>().ReportPreviousAsync(startup: true);

app.Logger.LogInformation("Inventory {Version}. Datenverzeichnis: {DataDirectory}, Simulation: {Simulate}, Anmeldung: {Authentication}",
    AppVersion.Current, dataDirectory, options.ResolveSimulate(), string.Join(" + ", new[]
    {
        windowsAuthentication ? $"Windows ({string.Join(", ", options.DescribeAllowedGroups())})" : null,
        options.OidcEnabled ? $"OIDC ({options.Oidc.ResolveAuthority()})" : null,
    }.Where(m => m is not null).DefaultIfEmpty("keine")));
if (!options.AuthenticationEnabled && !options.ResolveSimulate())
    app.Logger.LogWarning("Die Anmeldung ist abgeschaltet: Jeder, der diese Adresse erreicht, kann mit den hinterlegten Zugangsdaten Befehle auf den Zielrechnern ausführen.");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Vor den Statusseiten: deren erneute Ausführung der Anfrage würde den mehrstufigen Negotiate-Handshake zerstören.
if (options.AuthenticationEnabled)
    app.UseAuthentication();
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
app.MapAgentEndpoints();
app.MapInventoryLogin(options);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
