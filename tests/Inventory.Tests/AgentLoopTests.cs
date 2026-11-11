using System.Text;
using Inventory.Agent;
using Inventory.Contracts;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Tests;

/// <summary>
/// Die Agent-Schleife gegen den echten Server (Simulationsmodus): anmelden, Scan-Job abholen, Ergebnis abliefern. Die Plattform
/// ist nachgebildet; so gilt der Test für jeden Agenten, der die Schleife nutzt.
/// </summary>
public sealed class AgentLoopTests : IClassFixture<AgentLoopTests.App>
{
    private readonly App _app;

    public AgentLoopTests(App app) => _app = app;

    public sealed class App : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"inventory-agentloop-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Inventory:DataDirectory", DataDirectory);
            builder.UseSetting("Inventory:Simulate", "true");
            builder.UseSetting("Inventory:Update:Enabled", "false");
            // Kein eingebautes HTTPS: es hinge sonst davon ab, ob Port 5101 auf dem Testrechner frei ist.
            builder.UseSetting("Inventory:Https:Enabled", "false");
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
                // Ein Temp-Verzeichnis darf keinen Test scheitern lassen.
            }
        }
    }

    /// <summary>Beantwortet einen Inventar-Scan mit festen Daten.</summary>
    private sealed class FakePlatform(string directory, string hostName, string runtime) : IAgentPlatform
    {
        public string Directory { get; } = directory;
        public string Version => "9.9.9";
        public string Runtime { get; } = runtime;
        public string HostName { get; } = hostName;
        public string? DnsName() => null;
        public DateTime BootTimeUtc() => DateTime.UtcNow.AddHours(-1);
        public void PrepareUpdatedFile(string path) { }
        public void Describe(AgentEnrollRequest request) { }

        public IAgentJob CreateJob(string jobDir) => new Job(jobDir, HostName);

        private sealed class Job(string jobDir, string hostName) : IAgentJob
        {
            public JobResult Run()
            {
                var manifest = JobJson.Deserialize<JobManifest>(File.ReadAllText(Path.Combine(jobDir, JobFiles.Manifest), Encoding.UTF8))!;
                var step = manifest.Steps.Single();
                return new JobResult
                {
                    JobId = manifest.JobId, Success = step.Type is StepType.InventoryScan or StepType.UninstallAgent, StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow,
                    Steps = [new StepResult { Index = 0, Name = step.Name, Status = StepStatus.Succeeded }],
                    Inventory = step.Type == StepType.InventoryScan
                        ? new InventoryData { ComputerName = hostName, OsName = "Test OS 1.0", Manufacturer = "Test Inc.", MemoryBytes = 8L << 30 }
                        : null,
                };
            }

            public void Cancel() { }
        }
    }

    [Fact]
    public async Task An_agent_enrolls_picks_up_a_scan_and_delivers_the_inventory()
    {
        var agents = _app.Services.GetRequiredService<AgentService>();
        var runner = _app.Services.GetRequiredService<RunnerPackage>();
        var directory = Path.Combine(_app.DataDirectory, "agent");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, AgentProtocol.ConfigFile),
            JobJson.Serialize(new AgentConfig { ServerUrl = "http://localhost", EnrollmentToken = await agents.GetEnrollmentTokenAsync() }));
        // Auf dem Stand des Servers: sonst bekäme der Agent zuerst ein Update angeboten.
        File.WriteAllText(Path.Combine(directory, AgentProtocol.VersionFile), runner.For(RunnerRuntimes.Framework)!.Hash);

        var loop = new AgentLoop(new FakePlatform(directory, "AGENT-01", RunnerRuntimes.Framework), _app.Server.CreateHandler());
        var thread = new Thread(loop.Run) { IsBackground = true };
        thread.Start();
        try
        {
            var factory = _app.Services.GetRequiredService<IDbContextFactory<Inventory.Data.InventoryDbContext>>();
            int? id = null;
            for (var i = 0; i < 100 && id is null; i++)
            {
                await using (var db = await factory.CreateDbContextAsync())
                    id = (await db.Computers.FirstOrDefaultAsync(c => c.Name == "AGENT-01" && c.AgentKeyHash != null))?.Id;
                if (id is null)
                    await Task.Delay(100);
            }
            Assert.NotNull(id);

            // Kommt das Ergebnis nicht an, wartet der Server sonst bis zum Ablauf des Jobs.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var outcome = await _app.Services.GetRequiredService<ScanService>().ScanAsync(id.Value, timeout.Token);
            Assert.True(outcome.Success, outcome.Error);
            await using var check = await factory.CreateDbContextAsync();
            var computer = await check.Computers.SingleAsync(c => c.Id == id);
            Assert.Equal(("Test OS 1.0", "Test Inc."), (computer.OsName, computer.Manufacturer));
            Assert.Equal("9.9.9", computer.AgentVersion?.Split(' ')[0]);
        }
        finally
        {
            loop.Stop();
            // Läuft die Schleife weiter, arbeitete sie im Datenordner, den der Testaufbau gleich löscht.
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The agent loop did not stop.");
        }
    }

    [Fact]
    public async Task Removing_an_agent_forgets_it_and_it_does_not_enroll_again()
    {
        var agents = _app.Services.GetRequiredService<AgentService>();
        var runner = _app.Services.GetRequiredService<RunnerPackage>();
        var directory = Path.Combine(_app.DataDirectory, "agent-removed");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, AgentProtocol.ConfigFile),
            JobJson.Serialize(new AgentConfig { ServerUrl = "http://localhost", EnrollmentToken = await agents.GetEnrollmentTokenAsync() }));
        File.WriteAllText(Path.Combine(directory, AgentProtocol.VersionFile), runner.For(RunnerRuntimes.Framework)!.Hash);

        var loop = new AgentLoop(new FakePlatform(directory, "AGENT-02", RunnerRuntimes.Framework), _app.Server.CreateHandler());
        var thread = new Thread(loop.Run) { IsBackground = true };
        thread.Start();
        try
        {
            var factory = _app.Services.GetRequiredService<IDbContextFactory<Inventory.Data.InventoryDbContext>>();
            async Task<Inventory.Core.Model.Computer?> Load()
            {
                await using var db = await factory.CreateDbContextAsync();
                return await db.Computers.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "AGENT-02");
            }
            for (var i = 0; i < 100 && (await Load())?.AgentKeyHash is null; i++)
                await Task.Delay(100);
            var id = (await Load())!.Id;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await agents.RemoveAsync(id, _app.Services.GetRequiredService<RemoteJobService>(), timeout.Token);
            Assert.True(result.Success, result.Error);
            Assert.Null((await Load())!.AgentKeyHash);
            // Nach dem Entfernen fragt der Agent keine Jobs mehr ab: mit dem verworfenen Schlüssel meldete er sich sonst neu an.
            await Task.Delay(TimeSpan.FromSeconds(4));
            var after = (await Load())!;
            Assert.Equal((null, (Inventory.Core.Model.TransportKind?)null), (after.AgentKeyHash, after.Transport));
        }
        finally
        {
            loop.Stop();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The agent loop did not stop.");
        }
    }

    [Fact]
    public async Task An_agent_switches_to_the_offered_https_address_after_a_job_and_keeps_the_installed_one()
    {
        var agents = _app.Services.GetRequiredService<AgentService>();
        var runner = _app.Services.GetRequiredService<RunnerPackage>();
        var directory = Path.Combine(_app.DataDirectory, "agent-https");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, AgentProtocol.ConfigFile),
            JobJson.Serialize(new AgentConfig { ServerUrl = "http://localhost", EnrollmentToken = await agents.GetEnrollmentTokenAsync() }));
        File.WriteAllText(Path.Combine(directory, AgentProtocol.VersionFile), runner.For(RunnerRuntimes.Framework)!.Hash);
        // Der Testserver beantwortet jede Adresse; über https:// gilt die Anfrage als HTTPS.
        agents.OfferHttps("https://inventory.example.test:5101", ["cGlu"]);

        var loop = new AgentLoop(new FakePlatform(directory, "AGENT-03", RunnerRuntimes.Framework), _app.Server.CreateHandler());
        var thread = new Thread(loop.Run) { IsBackground = true };
        thread.Start();
        try
        {
            var factory = _app.Services.GetRequiredService<IDbContextFactory<Inventory.Data.InventoryDbContext>>();
            async Task<Inventory.Core.Model.Computer?> Load()
            {
                await using var db = await factory.CreateDbContextAsync();
                return await db.Computers.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "AGENT-03" && c.AgentKeyHash != null);
            }
            // Die erste Abfrage kommt über die Adresse aus der Installation, also HTTP.
            for (var i = 0; i < 100 && (await Load())?.AgentHttps is null; i++)
                await Task.Delay(100);
            var computer = (await Load())!;
            Assert.False(computer.AgentHttps);

            // Nach dem Job prüft der Agent die angebotene Adresse und wechselt.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            Assert.True((await _app.Services.GetRequiredService<ScanService>().ScanAsync(computer.Id, timeout.Token)).Success);
            for (var i = 0; i < 100 && (await Load())!.AgentHttps != true; i++)
                await Task.Delay(100);
            Assert.True((await Load())!.AgentHttps);
            var config = JobJson.Deserialize<AgentConfig>(File.ReadAllText(Path.Combine(directory, AgentProtocol.ConfigFile)))!;
            Assert.Equal(("http://localhost", "https://inventory.example.test:5101"), (config.ServerUrl, config.SecureUrl));
            Assert.Equal(["cGlu"], config.CertificatePins!);
            Assert.Contains("Verbindung zum Server jetzt über https://inventory.example.test:5101.", File.ReadAllText(Path.Combine(directory, "agent.log")));

            // Über HTTPS weiter Jobs.
            Assert.True((await _app.Services.GetRequiredService<ScanService>().ScanAsync(computer.Id, timeout.Token)).Success);
        }
        finally
        {
            agents.OfferHttps(null, []);
            loop.Stop();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The agent loop did not stop.");
        }
    }

    [Fact]
    public void The_https_address_is_used_until_it_fails_repeatedly_then_both_take_turns()
    {
        var config = new AgentConfig { ServerUrl = "http://inventory:5100/", SecureUrl = "https://inventory:5101/" };
        Assert.Equal(["https://inventory:5101", "https://inventory:5101", "https://inventory:5101", "http://inventory:5100", "http://inventory:5100",
            "http://inventory:5100", "https://inventory:5101"], Enumerable.Range(0, 7).Select(f => AgentLoop.ChooseUrl(config, f)));
        Assert.Equal("http://inventory:5100", AgentLoop.ChooseUrl(new AgentConfig { ServerUrl = "http://inventory:5100" }, 0));

        // Nach einem Erfolg bleibt der Agent in der Runde, die gerade gilt: über HTTPS bei HTTPS, im Rückfall bei der installierten Adresse.
        Assert.Equal([0, 0, 0, 3, 3, 3, 0], Enumerable.Range(0, 7).Select(AgentLoop.AfterSuccess));
        Assert.Equal("http://inventory:5100", AgentLoop.ChooseUrl(config, AgentLoop.AfterSuccess(4) + 1));
    }
}
