using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Smb;
using Inventory.Remoting.WinRm;
using Inventory.Server;
using Inventory.Server.Api;
using Inventory.Server.Components;
using Inventory.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

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
if (options.ResolveSimulate())
{
    builder.Services.AddSingleton<IHostProbe, SimulatedHostProbe>();
    builder.Services.AddSingleton<IRemoteTransport>(new SimulatedTransport(TransportKind.SmbService));
    builder.Services.AddSingleton<IRemoteTransport>(new SimulatedTransport(TransportKind.WinRm));
}
else
{
    builder.Services.AddSingleton<IHostProbe, TcpHostProbe>();
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton(new RunnerPackage(Path.Combine(AppContext.BaseDirectory, "runner")));
        builder.Services.AddSingleton<IRemoteTransport, SmbServiceTransport>();
        builder.Services.AddSingleton<IRemoteTransport, WinRmTransport>();
    }
}
builder.Services.AddSingleton<RemoteJobService>();
builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddSingleton<ScanService>();
builder.Services.AddSingleton<CollectionService>();
builder.Services.AddSingleton(new PackageRepository(Path.Combine(dataDirectory, "repository")));
builder.Services.AddSingleton<PackageService>();
builder.Services.AddSingleton<DeploymentService>();
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

app.Logger.LogInformation("Datenverzeichnis: {DataDirectory}, Simulation: {Simulate}", dataDirectory, options.ResolveSimulate());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapComputerEndpoints();
app.MapPackageEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
