using Inventory.Core.Abstractions;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
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
    builder.Services.AddSingleton<IHostProbe, SimulatedHostProbe>();
else
    builder.Services.AddSingleton<IHostProbe, TcpHostProbe>();
builder.Services.AddSingleton<ComputerService>();
builder.Services.AddSingleton<CredentialService>();
builder.Services.AddSingleton<SettingsService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<InventoryDbContext>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();

app.Logger.LogInformation("Datenverzeichnis: {DataDirectory}, Simulation: {Simulate}", dataDirectory, options.ResolveSimulate());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapComputerEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
