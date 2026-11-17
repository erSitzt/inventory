using System.Net;
using System.Security.Cryptography;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Agent;

namespace Inventory.Tests;

/// <summary>Remote-Befehl als Konto der Zugangsdaten: Auswahl der Zugangsdaten, Schlüsselaustausch mit dem Runner, Weg je Transport.</summary>
public class RunAsCredentialsTests
{
    private static JobManifest Job(StepRunAs runAs) => new() { Steps = [new JobStep { Type = StepType.PowerShell, Script = "whoami", RunAs = runAs }] };

    [Fact]
    public void Credentials_travel_encrypted_and_only_the_key_of_the_runner_opens_them()
    {
        using var runner = RSA.Create(JobSecrets.KeySize);
        // 300 Zeichen mit Umlauten: mehr als ein Block.
        var password = string.Concat(Enumerable.Repeat("Pässwört€-", 30));
        var answer = JobSecrets.Answer(JobSecrets.CreateRequest(runner), @"CORP\deploy", password);

        Assert.True(answer.Password.Count > 1);
        Assert.DoesNotContain("Pässwört", JobJson.Serialize(answer));
        Assert.Equal((@"CORP\deploy", password), JobSecrets.Decrypt(runner, answer));
        Assert.Equal(("u", ""), JobSecrets.Decrypt(runner, JobSecrets.Answer(JobSecrets.CreateRequest(runner), "u", "")));

        using var other = RSA.Create(JobSecrets.KeySize);
        Assert.ThrowsAny<CryptographicException>(() => JobSecrets.Decrypt(other, answer));
        using var weak = RSA.Create(1024);
        Assert.Throws<ArgumentException>(() => JobSecrets.Answer(JobSecrets.CreateRequest(weak), "u", "p"));
        Assert.Throws<ArgumentException>(() => JobSecrets.Answer(new SecretRequest { Modulus = "kein base64!", Exponent = "AQAB" }, "u", "p"));
    }

    [Fact]
    public void A_request_in_the_job_directory_is_answered_as_soon_as_a_readable_one_is_there()
    {
        var dir = Directory.CreateTempSubdirectory("inventory-secret-").FullName;
        try
        {
            Assert.False(JobSecrets.AnswerFile(dir, "u", "p"));
            File.WriteAllText(Path.Combine(dir, JobFiles.SecretRequest), "{ halb geschrieben");
            Assert.False(JobSecrets.AnswerFile(dir, "u", "p"));

            using var runner = RSA.Create(JobSecrets.KeySize);
            File.WriteAllText(Path.Combine(dir, JobFiles.SecretRequest), JobJson.Serialize(JobSecrets.CreateRequest(runner)));
            Assert.True(JobSecrets.AnswerFile(dir, @".\Administrator", "geheim"));

            var answer = JobJson.Deserialize<SecretAnswer>(File.ReadAllText(Path.Combine(dir, JobFiles.SecretAnswer)))!;
            Assert.Equal((@".\Administrator", "geheim"), JobSecrets.Decrypt(runner, answer));
            Assert.False(File.Exists(Path.Combine(dir, JobFiles.SecretAnswer + ".tmp")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"CORP\deploy", "deploy", "CORP")]
    [InlineData(@".\Administrator", "Administrator", ".")]
    [InlineData(@"\Administrator", "Administrator", ".")]
    [InlineData("Administrator", "Administrator", ".")]
    [InlineData("deploy@corp.local", "deploy@corp.local", null)]
    [InlineData(@"AzureAD\intune.admin", "intune.admin", "AzureAD")]
    public void Account_names_are_split_for_the_logon_on_the_target(string account, string user, string? domain) =>
        Assert.Equal((user, domain), JobSecrets.SplitAccount(account));

    [Fact]
    public void A_separately_stored_domain_is_part_of_the_account_name() =>
        Assert.Equal((@"CORP\deploy", "deploy"), (JobSecrets.Account(new NetworkCredential("deploy", "pw", "CORP")), JobSecrets.Account(new NetworkCredential("deploy", "pw"))));

    [Fact]
    public async Task Chosen_credentials_replace_the_ones_of_the_computer_for_this_run()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var credentials = new CredentialService(db, new ReversingProtector());
        var smb = new Recorder(TransportKind.SmbService);
        var jobs = new RemoteJobService([smb], computers, credentials, new SettingsService(db));
        var computer = (await computers.ImportAsync("pc-01"))[0];
        await credentials.SaveAsync(null, "Standard", @"CORP\deploy", "pw", null);
        var local = await credentials.SaveAsync(null, "Local admin", @".\Administrator", "pw2", null);

        Assert.True((await jobs.RunAsync(computer.Id, Job(StepRunAs.CredentialsUser), credentialId: local.Id)).Success);
        Assert.Equal((@".\Administrator", "pw2"), (smb.Target!.Credential!.UserName, smb.Target.Credential.Password));
        Assert.Equal(StepRunAs.CredentialsUser, smb.Job!.Steps[0].RunAs);

        // Ohne Auswahl wie bisher: die des Rechners, sonst der Standard.
        Assert.True((await jobs.RunAsync(computer.Id, Job(StepRunAs.System))).Success);
        Assert.Equal(@"CORP\deploy", smb.Target!.Credential!.UserName);

        smb.Target = null;
        var gone = await jobs.RunAsync(computer.Id, Job(StepRunAs.System), credentialId: 999);
        Assert.False(gone.Success);
        Assert.Contains("no longer exist", gone.Error);
        Assert.Null(smb.Target);
    }

    [Fact]
    public async Task Running_as_the_credentials_user_without_any_credentials_fails_before_reaching_the_computer()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var smb = new Recorder(TransportKind.SmbService);
        var jobs = new RemoteJobService([smb], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db));
        var computer = (await computers.ImportAsync("pc-01"))[0];

        var result = await jobs.RunAsync(computer.Id, Job(StepRunAs.CredentialsUser));
        Assert.False(result.Success);
        Assert.Contains("needs credentials", result.Error);
        Assert.Null(smb.Target);

        // Als Lokales System geht es ohne: dann gilt das Konto des Servers.
        Assert.True((await jobs.RunAsync(computer.Id, Job(StepRunAs.System))).Success);
    }

    [Fact]
    public async Task Over_winrm_the_runner_already_runs_as_the_credentials_user()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var credentials = new CredentialService(db, new ReversingProtector());
        var winRm = new Recorder(TransportKind.WinRm);
        var jobs = new RemoteJobService([winRm], computers, credentials, new SettingsService(db));
        var computer = (await computers.ImportAsync("pc-01"))[0];
        await computers.UpdateAsync(computer.Id, null, TransportKind.WinRm, null);
        await credentials.SaveAsync(null, "Standard", @"CORP\deploy", "pw", null);
        var job = Job(StepRunAs.CredentialsUser);

        Assert.True((await jobs.RunAsync(computer.Id, job)).Success);
        Assert.Equal(StepRunAs.System, winRm.Job!.Steps[0].RunAs);
        // Dasselbe Manifest kann noch an Rechner gehen, die über SMB oder den Agenten laufen.
        Assert.Equal(StepRunAs.CredentialsUser, job.Steps[0].RunAs);
    }

    [Fact]
    public void The_agent_hub_hands_out_the_credentials_once_and_only_for_jobs_that_need_them()
    {
        var hub = new AgentHub();
        var credential = new NetworkCredential(@"CORP\deploy", "pw");

        Assert.Null(hub.Enqueue("pc-01", Job(StepRunAs.System), [], null, credential).TakeSecret());
        var job = hub.Enqueue("pc-01", Job(StepRunAs.CredentialsUser), [], null, credential);
        Assert.Same(credential, job.TakeSecret());
        Assert.Null(job.TakeSecret());
    }

    private sealed class Recorder(TransportKind kind) : IRemoteTransport
    {
        public TransportKind Kind => kind;
        public RemoteTarget? Target { get; set; }
        public JobManifest? Job { get; private set; }

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            (Target, Job) = (target, job);
            return Task.FromResult(new JobResult { JobId = job.JobId, Success = true });
        }
    }
}
