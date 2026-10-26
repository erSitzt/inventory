using System.Net;
using System.Runtime.Versioning;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Smb;
using Inventory.Remoting.WinRm;

namespace Inventory.Tests;

[SupportedOSPlatform("windows")] // Die getesteten Hilfsfunktionen selbst sind plattformneutral.
public class TransportTests
{
    [Theory]
    [InlineData(@"CORP\deploy", "deploy", "CORP")]
    [InlineData(@".\admin", "admin", "pc-01")]
    [InlineData("admin", "admin", "pc-01")]
    [InlineData("deploy@corp.local", "deploy@corp.local", null)]
    public void SplitUsername_maps_local_accounts_to_target_host(string input, string user, string? domain)
    {
        Assert.Equal((user, domain), SmbServiceTransport.SplitUsername(new NetworkCredential(input, "x"), "pc-01"));
    }

    [Theory]
    [InlineData("3f2a9c0d1b2e4f5a8b7c6d5e4f3a2b1c", true)]
    [InlineData("job-01_A", true)]
    [InlineData("", false)]
    [InlineData(@"..\..\System32", false)]
    [InlineData("C:\\Windows", false)]
    [InlineData("a b", false)]
    public void Runner_service_accepts_only_plain_job_ids(string value, bool expected) =>
        Assert.Equal(expected, JobFiles.IsJobId(value));

    [Theory]
    [InlineData("\"C:\\WINDOWS\\Inventory\\Runner\\abc\\InventoryRunner.exe\" --service", true)]
    [InlineData("\"%SystemRoot%\\Inventory\\Runner\\abc\\InventoryRunner.exe\" --service", true)]
    [InlineData("\"D:\\Win\\inventory\\runner\\ABC\\inventoryrunner.exe\" --SERVICE", true)]
    [InlineData("\"C:\\WINDOWS\\Inventory\\Runner\\old\\InventoryRunner.exe\" --service", false)]
    [InlineData("\"C:\\WINDOWS\\Inventory\\Runner\\abc\\InventoryRunner.exe\" --service extra", false)]
    [InlineData("C:\\WINDOWS\\Inventory\\Runner\\abc\\InventoryRunner.exe --service", false)]
    [InlineData(null, false)]
    public void Runner_service_is_only_reconfigured_when_it_points_elsewhere(string? registered, bool same) =>
        Assert.Equal(same, Inventory.Remoting.Smb.SmbServiceTransport.SameRunner(registered,
            "\"%SystemRoot%\\Inventory\\Runner\\abc\\InventoryRunner.exe\" --service"));

    [Fact]
    public void New_job_ids_are_valid_for_the_runner_service() =>
        Assert.True(JobFiles.IsJobId(new JobManifest().JobId));

    [Fact]
    public void PumpProgress_reports_only_complete_lines_and_resumes_at_offset()
    {
        var path = Path.GetTempFileName();
        try
        {
            var events = new List<ProgressEvent>();
            var progress = new ListProgress(events);
            var line1 = JobJson.Serialize(new ProgressEvent { Kind = ProgressKind.Output, Text = "eins äöü" }) + "\n";
            var line2 = JobJson.Serialize(new ProgressEvent { Kind = ProgressKind.Output, Text = "zwei" }) + "\n";

            File.WriteAllText(path, line1 + line2[..10]);
            var offset = SmbServiceTransport.PumpProgress(path, 0, progress);
            Assert.Equal("eins äöü", Assert.Single(events).Text);

            File.WriteAllText(path, line1 + line2);
            offset = SmbServiceTransport.PumpProgress(path, offset, progress);
            Assert.Equal(["eins äöü", "zwei"], events.Select(e => e.Text));
            Assert.Equal(offset, SmbServiceTransport.PumpProgress(path, offset, progress));
            Assert.Equal(0, SmbServiceTransport.PumpProgress(path + ".fehlt", 0, progress));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JobService_uses_computer_transport_and_credential_and_reports_delivery_errors()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var credentials = new CredentialService(db, new ReversingProtector());
        var settings = new SettingsService(db);
        var smb = new RecordingTransport(TransportKind.SmbService);
        var jobs = new RemoteJobService([smb], computers, credentials, settings);
        var computer = (await computers.ImportAsync("pc-01"))[0];
        await credentials.SaveAsync(null, "Standard", @"CORP\deploy", "pw", null);
        var job = new JobManifest { Steps = [new JobStep { Type = StepType.Command, Script = "whoami" }] };

        Assert.True((await jobs.RunAsync(computer.Id, job)).Success);
        Assert.Equal(("pc-01", @"CORP\deploy", "pw"), (smb.Last!.Host, smb.Last.Credential!.UserName, smb.Last.Credential.Password));

        await computers.UpdateAsync(computer.Id, null, TransportKind.WinRm, null);
        Assert.Contains("WinRm", (await jobs.RunAsync(computer.Id, job)).Error);

        await computers.UpdateAsync(computer.Id, null, null, null);
        smb.Fail = "Zugriff verweigert";
        var failed = await jobs.RunAsync(computer.Id, job);
        Assert.False(failed.Success);
        Assert.Equal("Zugriff verweigert", failed.Error);
    }

    [Fact]
    public async Task JobService_falls_back_to_other_transport_only_when_enabled()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var settings = new SettingsService(db);
        var smb = new RecordingTransport(TransportKind.SmbService) { Fail = "Port 445 gesperrt" };
        var winRm = new RecordingTransport(TransportKind.WinRm);
        var jobs = new RemoteJobService([smb, winRm], computers, new CredentialService(db, new ReversingProtector()), settings);
        var computer = (await computers.ImportAsync("pc-01"))[0];
        var job = new JobManifest { Steps = [new JobStep { Type = StepType.Command, Script = "whoami" }] };

        Assert.Equal("Port 445 gesperrt", (await jobs.RunAsync(computer.Id, job)).Error);
        Assert.Null(winRm.Last);

        await settings.SetTransportFallbackAsync(true);
        Assert.True((await jobs.RunAsync(computer.Id, job)).Success);
        Assert.Equal("pc-01", winRm.Last!.Host);

        winRm.Fail = "WinRM aus";
        var failed = await jobs.RunAsync(computer.Id, job);
        Assert.Contains("Port 445 gesperrt", failed.Error);
        Assert.Contains("WinRM aus", failed.Error);
    }

    [Fact]
    public async Task JobService_does_not_fall_back_when_the_job_may_already_have_run()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var settings = new SettingsService(db);
        await settings.SetTransportFallbackAsync(true);
        var smb = new RecordingTransport(TransportKind.SmbService) { Fail = "kein Ergebnis", ResultMayFollow = true };
        var winRm = new RecordingTransport(TransportKind.WinRm);
        var jobs = new RemoteJobService([smb, winRm], computers, new CredentialService(db, new ReversingProtector()), settings);
        var computer = (await computers.ImportAsync("pc-01"))[0];

        var failed = await jobs.RunAsync(computer.Id, new JobManifest { Steps = [new JobStep { Type = StepType.Command, Script = "whoami" }] });

        Assert.Null(winRm.Last);
        Assert.Equal(("kein Ergebnis", true), (failed.Error, failed.ResultMayFollow));
    }

    [Fact]
    public async Task JobService_does_not_fall_back_when_the_computer_lacks_the_framework()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var settings = new SettingsService(db);
        await settings.SetTransportFallbackAsync(true);
        var smb = new RecordingTransport(TransportKind.SmbService) { Fail = "needs .NET Framework 4.8", NoFallback = true };
        var winRm = new RecordingTransport(TransportKind.WinRm);
        var jobs = new RemoteJobService([smb, winRm], computers, new CredentialService(db, new ReversingProtector()), settings);
        var computer = (await computers.ImportAsync("pc-01"))[0];

        var failed = await jobs.RunAsync(computer.Id, new JobManifest { Steps = [new JobStep { Type = StepType.InventoryScan }] });

        Assert.Null(winRm.Last);
        Assert.Equal("needs .NET Framework 4.8", failed.Error);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, "4.0")]
    [InlineData(379893, "4.5.2")]
    [InlineData(394802, "4.6.2")]
    [InlineData(461814, "4.7.2")]
    [InlineData(528372, "4.8")]
    [InlineData(533325, "4.8.1")]
    public void Framework_release_values_are_named(int release, string? name) => Assert.Equal(name, NetFramework.NameForRelease(release));

    [Theory]
    [InlineData(4, 0, 30319, 1, "4.0", false)]
    [InlineData(4, 0, 30319, 17929, "4.5", false)]
    [InlineData(4, 0, 30319, 18408, "4.5.1", false)]
    [InlineData(4, 0, 30319, 34209, "4.5.2", false)]
    [InlineData(4, 7, 3062, 0, "4.7", false)]
    [InlineData(4, 8, 4645, 0, "4.8", true)]
    [InlineData(4, 8, 9032, 0, "4.8", true)]
    public void Framework_is_judged_by_the_clr_file_version(int major, int minor, int build, int revision, string name, bool sufficient) =>
        Assert.Equal((name, sufficient), NetFramework.FromClrVersion(major, minor, build, revision));

    [Fact]
    public void Framework_check_over_admin_share_reads_clr_dll()
    {
        var share = Path.Combine(Path.GetTempPath(), "netfx-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Ohne lesbaren Microsoft.NET-Ordner lässt sich nichts sagen: der Start des Runners entscheidet.
            Assert.Null(NetFramework.Inspect(share));
            Directory.CreateDirectory(Path.Combine(share, "Microsoft.NET", "Framework64"));
            // 32 Bit (kein SysWOW64): auch die eigenständige Variante läuft dort nicht.
            Assert.Equal(new FrameworkState(false, null, false), NetFramework.Inspect(share));
            // Der Ordner von .NET 4 ist da, clr.dll aber nicht lesbar: lieber nichts behaupten.
            Directory.CreateDirectory(Path.Combine(share, "Microsoft.NET", "Framework64", "v4.0.30319"));
            Assert.Null(NetFramework.Inspect(share));
        }
        finally
        {
            Directory.Delete(share, recursive: true);
        }
    }

    [Theory]
    [InlineData(6, 1, 7601, false)]
    [InlineData(6, 2, 9200, true)]
    [InlineData(6, 3, 9600, true)]
    [InlineData(10, 0, 10240, false)]
    [InlineData(10, 0, 10586, false)]
    [InlineData(10, 0, 14393, true)]
    [InlineData(10, 0, 26100, true)]
    public void Self_contained_runner_needs_server_2012_or_windows_10_1607(int major, int minor, int build, bool supported) =>
        Assert.Equal(supported, NetFramework.SupportsSelfContained(major, minor, build));

    [Fact]
    public void The_large_runner_is_only_chosen_without_framework_4_8()
    {
        var root = Path.Combine(Path.GetTempPath(), "runners-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (folder, content) in new[] { ("small", "net48"), ("large", "net10") })
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
                File.WriteAllText(Path.Combine(root, folder, Inventory.Contracts.JobFiles.RunnerExe), content);
            }
            var runner = new RunnerPackage(Path.Combine(root, "small"), Path.Combine(root, "large"));
            var L = Inventory.Core.PassthroughLocalizer.Instance;

            Assert.Same(runner, NetFramework.Choose(runner, null, L));
            Assert.Same(runner, NetFramework.Choose(runner, new FrameworkState(true, "4.8", false), L));
            Assert.Same(runner.SelfContained, NetFramework.Choose(runner, new FrameworkState(false, "4.5", true), L));
            Assert.Equal(Inventory.Contracts.RunnerRuntimes.SelfContained, runner.SelfContained!.Runtime);
            Assert.Equal([runner.ExeSha256, runner.SelfContained.ExeSha256], runner.AllExeSha256);
            Assert.Same(runner.SelfContained, runner.For("net10"));
            Assert.Same(runner, runner.For(null));

            // Windows 7 / Server 2008 R2: nur .NET Framework 4.8 hilft, ein anderer Transport scheiterte genauso.
            var old = Assert.Throws<RemoteException>(() => NetFramework.Choose(runner, new FrameworkState(false, "4.0", false), L));
            Assert.True(old.NoFallback);
            Assert.Contains("only has .NET Framework 4.0", old.Message);
            // Ein Server ohne eigenständige Variante bleibt bei der Meldung.
            var plain = new RunnerPackage(Path.Combine(root, "small"));
            Assert.Throws<RemoteException>(() => NetFramework.Choose(plain, new FrameworkState(false, "4.5", true), L));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WinRm_reports_a_missing_framework_with_the_installed_version()
    {
        Assert.Contains("only has .NET Framework 4.5.2", WinRmTransport.Explain("NETFX_MISSING:379893"));
        Assert.Contains("only has .NET Framework 4.0", WinRmTransport.Explain("Fehler: NETFX_MISSING:0"));
        Assert.Contains("no .NET Framework 4", WinRmTransport.Explain("NETFX_MISSING:-1"));
    }

    [Fact]
    public async Task Connections_use_the_dns_name_while_the_agent_keeps_the_name_of_the_entry()
    {
        using var db = new TestDb();
        var probed = new List<string>();
        var computers = new ComputerService(db, new FixedProbe(host =>
        {
            lock (probed)
                probed.Add(host);
            return new ProbeResult(true, "10.0.0.1");
        }));
        var settings = new SettingsService(db);
        var smb = new RecordingTransport(TransportKind.SmbService);
        var agent = new RecordingTransport(TransportKind.Agent);
        var jobs = new RemoteJobService([smb, agent], computers, new CredentialService(db, new ReversingProtector()), settings);
        var added = await computers.ImportAsync("pc-01 pc-02 pc-03.other.example pc-04");
        await using (var context = db.CreateDbContext())
        {
            foreach (var computer in context.Computers.Where(c => c.Name != "pc-04"))
                computer.DnsName = computer.Name.Split('.')[0] + ".corp.example";
            await context.SaveChangesAsync();
        }
        await computers.UpdateAsync(added.Single(c => c.Name == "pc-02").Id, null, TransportKind.Agent, null);
        JobManifest Job() => new() { Steps = [new JobStep { Type = StepType.InventoryScan }] };
        async Task<string> HostAsync(string name, RecordingTransport via)
        {
            Assert.True((await jobs.RunAsync(added.Single(c => c.Name == name).Id, Job())).Success);
            return via.Last!.Host;
        }

        Assert.Equal("pc-01.corp.example", await HostAsync("pc-01", smb));
        // Der Agent holt seine Jobs unter dem Namen des Eintrags ab.
        Assert.Equal("pc-02", await HostAsync("pc-02", agent));
        // Ein von Hand eingetragener DNS-Name gilt, und ohne bekannten DNS-Namen bleibt es beim kurzen.
        Assert.Equal("pc-03.other.example", await HostAsync("pc-03.other.example", smb));
        Assert.Equal("pc-04", await HostAsync("pc-04", smb));

        await computers.ProbeAsync();
        Assert.Equal(["pc-01.corp.example", "pc-02.corp.example", "pc-03.other.example", "pc-04"], probed.Order());
    }

    [Fact]
    public async Task JobService_reaches_computers_without_agent_only_as_far_as_the_setting_allows()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var settings = new SettingsService(db);
        var smb = new RecordingTransport(TransportKind.SmbService) { Fail = "Port 445 gesperrt" };
        var winRm = new RecordingTransport(TransportKind.WinRm);
        var agent = new RecordingTransport(TransportKind.Agent);
        var jobs = new RemoteJobService([smb, winRm, agent], computers, new CredentialService(db, new ReversingProtector()), settings);
        var plain = (await computers.ImportAsync("pc-01"))[0];
        var managed = (await computers.ImportAsync("pc-02"))[0];
        await computers.UpdateAsync(managed.Id, null, TransportKind.Agent, null);
        await settings.SetTransportFallbackAsync(true);
        JobManifest Job() => new() { Steps = [new JobStep { Type = StepType.InventoryScan }] };
        await settings.SetAsync(SettingKeys.AgentServerUrl, "https://inventory.example.local:5100");
        var install = await new AgentService(db, settings, TimeProvider.System).BuildInstallJobAsync();

        // Nur die Installation des Agenten: sie darf agentenlos laufen, auch auf Rechnern, die schon einen Agenten haben.
        await settings.SetAgentlessPolicyAsync(AgentlessPolicy.AgentInstallOnly);
        Assert.Contains("only allowed for installing the agent", (await jobs.RunAsync(plain.Id, Job())).Error);
        Assert.Null(smb.Last);
        Assert.Null(winRm.Last);
        Assert.True((await jobs.RunAgentInstallAsync(plain.Id, install)).Success);
        Assert.Equal("pc-01", winRm.Last!.Host);
        Assert.True((await jobs.RunAgentInstallAsync(managed.Id, install)).Success);
        Assert.Equal("pc-02", winRm.Last!.Host);
        Assert.True((await jobs.RunAsync(managed.Id, Job())).Success);
        Assert.Equal("pc-02", agent.Last!.Host);

        // Ganz aus: auch die Installation und der Ausweichweg bleiben gesperrt, der Agent läuft weiter.
        await settings.SetAgentlessPolicyAsync(AgentlessPolicy.Never);
        (smb.Last, winRm.Last, agent.Last) = (null, null, null);
        Assert.Contains("switched off", (await jobs.RunAsync(plain.Id, Job())).Error);
        Assert.Contains("switched off", (await jobs.RunAgentInstallAsync(managed.Id, install)).Error);
        // Die Ausnahme gilt nur für Jobs, die der AgentService als Installation gebaut hat.
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.RunAgentInstallAsync(plain.Id, new BuiltJob(Job(), [])));
        Assert.Null(smb.Last);
        Assert.Null(winRm.Last);
        Assert.True((await jobs.RunAsync(managed.Id, Job())).Success);
        Assert.Equal("pc-02", agent.Last!.Host);

        Assert.Equal(AgentlessPolicy.Always, await new SettingsService(new TestDb()).GetAgentlessPolicyAsync());
    }

    [Theory]
    [InlineData("P:{\"kind\":\"Output\"}", "Progress", "{\"kind\":\"Output\"}")]
    [InlineData("R:eyJ9", "Result", "eyJ9")]
    [InlineData("E:Zugriff verweigert", "Error", "Zugriff verweigert")]
    [InlineData("irgendeine Warnung", "Other", "irgendeine Warnung")]
    public void WinRm_output_lines_are_classified(string line, string kind, string payload)
    {
        var parsed = WinRmTransport.Parse(line);

        Assert.Equal((kind, payload), (parsed.Kind.ToString(), parsed.Payload));
    }

    [Fact]
    public void WinRm_errors_are_made_readable()
    {
        Assert.Contains("constrained language mode", WinRmTransport.Explain("CONSTRAINED_LOCAL"));
        Assert.Contains("target computer", WinRmTransport.Explain("Fehler: CONSTRAINED_REMOTE"));
        Assert.Equal("Zugriff verweigert", WinRmTransport.Explain("Zugriff verweigert"));
        Assert.Equal("New-Object : Fehler &  mehr", WinRmTransport.ErrorText("#< CLIXML\n<Objs><S S=\"Error\">New-Object : Fehler &amp; _x000D__x000A_</S><S S=\"Error\">mehr</S></Objs>"));
        Assert.Equal("klartext", WinRmTransport.ErrorText(" klartext "));
    }

    private sealed class ListProgress(List<ProgressEvent> events) : IProgress<ProgressEvent>
    {
        public void Report(ProgressEvent value) => events.Add(value);
    }

    private sealed class RecordingTransport(TransportKind kind) : IRemoteTransport
    {
        public TransportKind Kind => kind;
        public RemoteTarget? Last { get; set; }
        public string? Fail { get; set; }
        /// <summary>Der Fehler sagt, dass der Job womöglich schon lief (Agent mitten im Job verschwunden).</summary>
        public bool ResultMayFollow { get; set; }
        /// <summary>Der Fehler liegt am Rechner selbst (zu altes .NET Framework), nicht am Weg dorthin.</summary>
        public bool NoFallback { get; set; }

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<Inventory.Core.Abstractions.JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            Last = target;
            if (Fail is not null)
                throw new RemoteException(Fail, resultMayFollow: ResultMayFollow, noFallback: NoFallback);
            return Task.FromResult(new JobResult { JobId = job.JobId, Success = true });
        }
    }
}

public class NameCheckTests
{
    /// <summary>Baut eine NTLM-Challenge mit den angegebenen Namen, wie sie ein Windows-Rechner schickt.</summary>
    private static byte[] Challenge(string netbios, string dns)
    {
        static byte[] Pair(ushort id, string value)
        {
            var text = System.Text.Encoding.Unicode.GetBytes(value);
            return [(byte)id, 0, (byte)text.Length, 0, .. text];
        }
        byte[] info = [.. Pair(2, "CORP"), .. Pair(1, netbios), .. Pair(3, dns), 0, 0, 0, 0];
        var ntlm = new byte[48];
        "NTLMSSP\0"u8.CopyTo(ntlm);
        ntlm[8] = 2;
        ntlm[40] = (byte)info.Length;
        ntlm[44] = 48;
        // Davor steht in echten Antworten der SMB-Kopf samt SPNEGO-Hülle.
        return [.. new byte[90], .. ntlm, .. info];
    }

    [Fact]
    public void Machine_names_are_read_from_the_ntlm_challenge()
    {
        var identity = SmbNameQuery.ParseChallenge(Challenge("PC-07", "pc-07.corp.local"));

        Assert.Equal(new MachineIdentity("PC-07", "pc-07.corp.local"), identity);
        Assert.Null(SmbNameQuery.ParseChallenge(new byte[200]));
        Assert.Null(SmbNameQuery.ParseChallenge("NTLMSSP\0"u8.ToArray()));
    }

    [Theory]
    [InlineData("pc-07", "PC-07", null, false, false)]
    [InlineData("kasse-alt", "PC-07", null, true, false)]
    [InlineData("kasse-alt", null, "pc-07.corp.local", false, true)]
    [InlineData("pc-07.corp.local", null, "pc-07.corp.local", false, false)]
    [InlineData("ein-sehr-langer-rechnername", "EIN-SEHR-LANGER", null, false, false)]
    [InlineData("10.0.0.7", "PC-07", "pc-07.corp.local", false, false)]
    public void Mismatch_uses_the_machines_own_name_first_and_reverse_dns_only_as_a_hint(string entered, string? reported, string? reverse, bool mismatch, bool hint)
    {
        var computer = new Computer { Name = entered, ReportedName = reported, ReverseDnsName = reverse };

        Assert.Equal((mismatch, hint), (computer.NameMismatch, computer.ReverseDnsMismatch));
    }

    [Fact]
    public async Task Probe_stores_the_answering_machine_and_jobs_carry_the_expected_name_except_pure_scans()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new FixedProbe(_ => new ProbeResult(true, "10.0.0.7", null, "PC-07", "pc-07.corp.local")));
        var settings = new SettingsService(db);
        var transport = new CapturingTransport();
        var jobs = new RemoteJobService([transport], computers, new CredentialService(db, new ReversingProtector()), settings);
        var added = await computers.ImportAsync("kasse-alt pc-07");

        await computers.ProbeAsync();
        var stored = await computers.ListAsync();
        Assert.Equal([true, false], stored.Select(c => c.NameMismatch));
        Assert.All(stored, c => Assert.Equal("pc-07.corp.local", c.ReverseDnsName));
        // Unter dem Alias antwortet ein anderer Rechner: der eingetragene gilt nicht als online.
        Assert.Equal([false, true], stored.Select(c => c.IsOnline));
        Assert.Equal([false, true], stored.Select(c => c.LastSeenUtc is not null));

        await jobs.RunAsync(added[0].Id, new JobManifest { Steps = [new JobStep { Type = StepType.Command, Script = "x" }] });
        Assert.Equal("kasse-alt", transport.Last!.ExpectedComputerName);
        await jobs.RunAsync(added[0].Id, new JobManifest { Steps = [new JobStep { Type = StepType.InventoryScan }] });
        Assert.Null(transport.Last.ExpectedComputerName);

        await settings.SetVerifyTargetNameAsync(false);
        await computers.ProbeAsync();
        Assert.All(await computers.ListAsync(), c => Assert.True(c.IsOnline));
        await jobs.RunAsync(added[0].Id, new JobManifest { Steps = [new JobStep { Type = StepType.Command, Script = "x" }] });
        Assert.Null(transport.Last.ExpectedComputerName);

        // Der Alias kann nicht umbenannt werden, weil der echte Rechner schon eingetragen ist.
        await Assert.ThrowsAsync<InvalidOperationException>(() => computers.RenameToReportedAsync(added[0].Id));
        await computers.DeleteAsync([added[1].Id]);
        Assert.Equal("PC-07", await computers.RenameToReportedAsync(added[0].Id));
        Assert.False((await computers.ListAsync()).Single().NameMismatch);
    }

    private sealed class CapturingTransport : IRemoteTransport
    {
        public TransportKind Kind => TransportKind.SmbService;
        public JobManifest? Last { get; private set; }

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            Last = job;
            return Task.FromResult(new JobResult { JobId = job.JobId, Success = true });
        }
    }
}
