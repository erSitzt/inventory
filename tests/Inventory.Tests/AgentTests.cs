using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Agent;

namespace Inventory.Tests;

public class AgentTests
{
    private static readonly JobManifest Job = new() { Steps = [new JobStep { Type = StepType.Command, Script = "whoami", TimeoutSeconds = 60 }] };

    [Fact]
    public async Task Enrollment_requires_the_token_links_existing_computers_and_switches_them_to_the_agent()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var agents = new AgentService(db, new SettingsService(db), TimeProvider.System);
        await computers.ImportAsync("pc-01.corp.local");
        var token = await agents.GetEnrollmentTokenAsync();

        Assert.Null(await agents.EnrollAsync(new AgentEnrollRequest { EnrollmentToken = "falsch", HostName = "PC-01" }));
        var key = await agents.EnrollAsync(new AgentEnrollRequest { EnrollmentToken = token, HostName = "PC-01", Version = "1.0" });
        var newKey = await agents.EnrollAsync(new AgentEnrollRequest { EnrollmentToken = token, HostName = "NEU-02", Version = "1.0" });

        var stored = await computers.ListAsync();
        Assert.Equal(["NEU-02", "pc-01.corp.local"], stored.Select(c => c.Name));
        Assert.All(stored, c => Assert.Equal(TransportKind.Agent, c.Transport));
        Assert.DoesNotContain(stored, c => c.AgentKeyHash == key);
        Assert.Equal("pc-01.corp.local", (await agents.AuthenticateAsync(key, "1.1"))!.Name);
        Assert.Equal("1.1", (await computers.ListAsync()).Single(c => c.Name.StartsWith("pc-01")).AgentVersion);
        Assert.Equal("NEU-02", (await agents.AuthenticateAsync(newKey, null))!.Name);
        Assert.Null(await agents.AuthenticateAsync("unbekannt", null));

        // Ein erneuertes Geheimnis sperrt neue Anmeldungen, nicht vorhandene Agenten.
        await agents.RenewEnrollmentTokenAsync();
        Assert.Null(await agents.EnrollAsync(new AgentEnrollRequest { EnrollmentToken = token, HostName = "PC-03" }));
        Assert.NotNull(await agents.AuthenticateAsync(key, null));

        await agents.ForgetAsync(stored[1].Id);
        Assert.Null(await agents.AuthenticateAsync(key, null));
    }

    [Fact]
    public async Task Job_travels_through_the_hub_to_the_agent_and_back()
    {
        var hub = new AgentHub();
        var transport = new AgentTransport(hub);
        var events = new List<string?>();
        var running = transport.RunAsync(new RemoteTarget("PC-01", null), Job, [new JobFile("/tmp/a.msi", @"p1\a.msi")], new ListProgress(events), CancellationToken.None);

        Assert.Null(await hub.DequeueAsync("pc-02", TimeSpan.FromMilliseconds(50), CancellationToken.None));
        var offered = await hub.DequeueAsync("pc-01", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Same(Job, offered!.Manifest);
        Assert.Null(hub.Find(Job.JobId, "pc-02"));

        var found = hub.Find(Job.JobId, "PC-01")!;
        found.Progress!.Report(new ProgressEvent { Kind = ProgressKind.Output, Text = "nt authority\\system" });
        hub.Complete(found, new JobResult { JobId = Job.JobId, Success = true });

        Assert.True((await running).Success);
        Assert.Equal(["nt authority\\system"], events);
        Assert.Null(hub.Find(Job.JobId, "PC-01"));
    }

    [Fact]
    public async Task Job_fails_when_no_agent_picks_it_up_and_is_not_delivered_later()
    {
        var hub = new AgentHub();
        var transport = new AgentTransport(hub) { PickupTimeout = TimeSpan.FromMilliseconds(100) };

        var error = await Assert.ThrowsAsync<RemoteException>(() => transport.RunAsync(new RemoteTarget("PC-01", null), Job, [], null, CancellationToken.None));

        Assert.Contains("did not pick up", error.Message);
        Assert.Null(await hub.DequeueAsync("PC-01", TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact]
    public async Task Install_job_needs_a_server_url_and_carries_runner_files_and_config()
    {
        using var db = new TestDb();
        var agents = new AgentService(db, new SettingsService(db), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agents.BuildInstallJobAsync(["/srv/runner/InventoryRunner.exe"], "abc123"));

        await agents.SetServerUrlAsync("https://inventory.corp.local:5100/");
        var job = await agents.BuildInstallJobAsync(["/srv/runner/InventoryRunner.exe", "/srv/runner/System.Text.Json.dll"], "abc123");

        Assert.Equal([@"agent\InventoryRunner.exe", @"agent\System.Text.Json.dll"], job.Files.Select(f => f.RelativePath));
        var script = Assert.Single(job.Manifest.Steps).Script;
        Assert.Contains("\"serverUrl\":\"https://inventory.corp.local:5100\"", script);
        Assert.Contains(await agents.GetEnrollmentTokenAsync(), script);
        Assert.Contains("--agent", script);
        Assert.Contains("agent.version') -Value 'abc123'", script);
        Assert.Equal("0.1.0.0 (abc123)", AgentService.DescribeVersion("0.1.0.0", "abc123"));
        Assert.Equal("0.1.0.0", AgentService.DescribeVersion("0.1.0.0", ""));
        Assert.DoesNotContain("\n            $", script);
    }

    private sealed class ListProgress(List<string?> events) : IProgress<ProgressEvent>
    {
        public void Report(ProgressEvent value) => events.Add(value.Text);
    }
}
