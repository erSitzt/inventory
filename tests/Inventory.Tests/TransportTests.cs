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
        Assert.Contains("eingeschränkten Sprachmodus", WinRmTransport.Explain("CONSTRAINED_LOCAL"));
        Assert.Contains("Zielrechner", WinRmTransport.Explain("Fehler: CONSTRAINED_REMOTE"));
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
        public RemoteTarget? Last { get; private set; }
        public string? Fail { get; set; }

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<Inventory.Core.Abstractions.JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            Last = target;
            if (Fail is not null)
                throw new RemoteException(Fail);
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

        await jobs.RunAsync(added[0].Id, new JobManifest { Steps = [new JobStep { Type = StepType.Command, Script = "x" }] });
        Assert.Equal("kasse-alt", transport.Last!.ExpectedComputerName);
        await jobs.RunAsync(added[0].Id, new JobManifest { Steps = [new JobStep { Type = StepType.InventoryScan }] });
        Assert.Null(transport.Last.ExpectedComputerName);

        await settings.SetVerifyTargetNameAsync(false);
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
