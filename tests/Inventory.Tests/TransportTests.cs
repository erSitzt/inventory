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
