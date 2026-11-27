using System.Net;
using System.Security.Cryptography;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
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
            string? answered = null;
            NetworkCredential? Administrator(int? id) => id is null ? new NetworkCredential(@".\Administrator", "geheim") : null;
            Assert.False(JobSecrets.AnswerFile(dir, Administrator, ref answered));
            File.WriteAllText(Path.Combine(dir, JobFiles.SecretRequest), "{ halb geschrieben");
            Assert.False(JobSecrets.AnswerFile(dir, Administrator, ref answered));

            using var runner = RSA.Create(JobSecrets.KeySize);
            File.WriteAllText(Path.Combine(dir, JobFiles.SecretRequest), JobJson.Serialize(JobSecrets.CreateRequest(runner)));
            Assert.True(JobSecrets.AnswerFile(dir, Administrator, ref answered));

            var answer = JobJson.Deserialize<SecretAnswer>(File.ReadAllText(Path.Combine(dir, JobFiles.SecretAnswer)))!;
            Assert.Equal((@".\Administrator", "geheim"), JobSecrets.Decrypt(runner, answer));
            Assert.False(File.Exists(Path.Combine(dir, JobFiles.SecretAnswer + ".tmp")));
            // Bis der Runner die Antwort gelesen hat, liegt seine Anfrage noch da: kein zweites Mal beantworten.
            Assert.False(JobSecrets.AnswerFile(dir, Administrator, ref answered));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Each_account_a_step_asks_for_gets_its_own_answer_and_other_accounts_none()
    {
        var dir = Directory.CreateTempSubdirectory("inventory-secret-").FullName;
        try
        {
            var target = new RemoteTarget("pc-01", new NetworkCredential(@"CORP\scan", "pw1"), new Dictionary<int, NetworkCredential> { [7] = new(@"CORP\backup", "pw7") });
            string? answered = null;
            (string User, string Password)? Ask(int? credentialId)
            {
                using var runner = RSA.Create(JobSecrets.KeySize);
                var request = JobSecrets.CreateRequest(runner);
                request.CredentialId = credentialId;
                File.WriteAllText(Path.Combine(dir, JobFiles.SecretRequest), JobJson.Serialize(request));
                var answerPath = Path.Combine(dir, JobFiles.SecretAnswer);
                if (!JobSecrets.AnswerFile(dir, target.RunAsAccount, ref answered))
                    return null;
                var answer = JobJson.Deserialize<SecretAnswer>(File.ReadAllText(answerPath))!;
                File.Delete(answerPath);
                return JobSecrets.Decrypt(runner, answer);
            }

            Assert.Equal((@"CORP\scan", "pw1"), Ask(null));
            Assert.Equal((@"CORP\backup", "pw7"), Ask(7));
            // Zugangsdaten, nach denen kein Schritt des Jobs fragt, gibt der Server nicht heraus.
            Assert.Null(Ask(8));
            Assert.False(File.Exists(Path.Combine(dir, JobFiles.SecretAnswer)));
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
        var target = new RemoteTarget("pc-01", new NetworkCredential(@"CORP\deploy", "pw"),
            new Dictionary<int, NetworkCredential> { [7] = new(@"CORP\backup", "pw7"), [8] = new(@"CORP\other", "pw8") });

        Assert.Null(hub.Enqueue("pc-01", Job(StepRunAs.System), [], null, target.RunAsAccount).TakeSecret());
        var job = hub.Enqueue("pc-01", Job(StepRunAs.CredentialsUser), [], null, target.RunAsAccount);
        Assert.Same(target.Credential, job.TakeSecret());
        Assert.Null(job.TakeSecret());

        // Je Konto einmal, und nur Konten, als die ein Schritt des Jobs läuft.
        var twoAccounts = Job(StepRunAs.CredentialsUser);
        twoAccounts.Steps.Add(new JobStep { Type = StepType.PowerShell, Script = "whoami", RunAs = StepRunAs.CredentialsUser, CredentialId = 7 });
        job = hub.Enqueue("pc-01", twoAccounts, [], null, target.RunAsAccount);
        Assert.Null(job.TakeSecret(8));
        Assert.Same(target.Accounts![7], job.TakeSecret(7));
        Assert.Null(job.TakeSecret(7));
        Assert.Same(target.Credential, job.TakeSecret());
    }

    [Fact]
    public async Task A_package_step_runs_as_its_own_credentials_while_the_computer_is_reached_with_its_usual_ones()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var credentials = new CredentialService(db, new ReversingProtector());
        var smb = new Recorder(TransportKind.SmbService);
        var jobs = new RemoteJobService([smb], computers, credentials, new SettingsService(db));
        var computer = (await computers.ImportAsync("pc-01"))[0];
        await credentials.SaveAsync(null, "Scan", @"CORP\scan", "pw", null);
        var backup = await credentials.SaveAsync(null, "Backup", @"CORP\backup", "pw2", null);
        await credentials.SaveAsync(null, "Unused", @"CORP\unused", "pw3", null);
        var job = Job(StepRunAs.System);
        job.Steps.Add(new JobStep { Name = "Copy from the share", Type = StepType.PowerShell, Script = "whoami", RunAs = StepRunAs.CredentialsUser, CredentialId = backup.Id });

        Assert.True((await jobs.RunAsync(computer.Id, job)).Success);
        Assert.Equal(@"CORP\scan", smb.Target!.Credential!.UserName);
        Assert.Equal([backup.Id], smb.Target.Accounts!.Keys);
        Assert.Equal((@"CORP\backup", "pw2"), (smb.Target.RunAsAccount(backup.Id)!.UserName, smb.Target.RunAsAccount(backup.Id)!.Password));

        // Gelöscht, während der Job schon bereitlag: Er scheitert, bevor er den Rechner erreicht, statt als anderes Konto zu laufen.
        await credentials.DeleteAsync(backup.Id);
        smb.Target = null;
        var gone = await jobs.RunAsync(computer.Id, job);
        Assert.False(gone.Success);
        Assert.Contains("\"Copy from the share\" runs as credentials that no longer exist", gone.Error);
        Assert.Null(smb.Target);
    }

    [Fact]
    public async Task Over_winrm_a_step_cannot_run_as_another_account_than_the_one_that_connects()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var credentials = new CredentialService(db, new ReversingProtector());
        var winRm = new Recorder(TransportKind.WinRm);
        var smb = new Recorder(TransportKind.SmbService);
        var settings = new SettingsService(db);
        var jobs = new RemoteJobService([winRm, smb], computers, credentials, settings);
        var computer = (await computers.ImportAsync("pc-01"))[0];
        await computers.UpdateAsync(computer.Id, null, TransportKind.WinRm, null);
        var scan = await credentials.SaveAsync(null, "Scan", @"CORP\scan", "pw", null);
        var backup = await credentials.SaveAsync(null, "Backup", @"CORP\backup", "pw2", null);
        JobManifest As(int credentialId) => new() { Steps = [new JobStep { Name = "Backup", Type = StepType.PowerShell, Script = "whoami", RunAs = StepRunAs.CredentialsUser, CredentialId = credentialId }] };

        // Dasselbe Konto wie die Verbindung: läuft dort ohnehin so.
        Assert.True((await jobs.RunAsync(computer.Id, As(scan.Id))).Success);
        Assert.Equal((StepRunAs.System, (int?)null), (winRm.Job!.Steps[0].RunAs, winRm.Job.Steps[0].CredentialId));

        // Ein anderes Konto: gestartet ist nichts, also darf SMB es übernehmen.
        await settings.SetTransportFallbackAsync(true);
        Assert.True((await jobs.RunAsync(computer.Id, As(backup.Id))).Success);
        Assert.Equal((StepRunAs.CredentialsUser, backup.Id), (smb.Job!.Steps[0].RunAs, smb.Job.Steps[0].CredentialId));

        await settings.SetTransportFallbackAsync(false);
        winRm.Job = null;
        var refused = await jobs.RunAsync(computer.Id, As(backup.Id));
        Assert.False(refused.Success);
        Assert.Contains(@"runs as CORP\backup. Over WinRM it can only run as the account that connects (CORP\scan)", refused.Error);
        Assert.Null(winRm.Job);
    }

    [Fact]
    public async Task A_package_step_keeps_its_credentials_only_while_it_runs_as_them_and_they_cannot_vanish_under_it()
    {
        using var db = new TestDb();
        var repository = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
        try
        {
            var credentials = new CredentialService(db, new ReversingProtector());
            var packages = new PackageService(db, new PackageRepository(repository));
            await credentials.SaveAsync(null, "Scan", @"CORP\scan", "pw", null);
            var backup = await credentials.SaveAsync(null, "Backup", @"CORP\backup", "pw2", null);
            var package = await packages.SaveAsync(null, "Backup client", "1.0", null);
            JobStep Step(StepType type, StepRunAs runAs, int? credentialId) => new() { Name = "Step", Type = type, Script = "whoami", RunAs = runAs, CredentialId = credentialId };

            await packages.SaveStepAsync(package.Id, null, Step(StepType.PowerShell, StepRunAs.CredentialsUser, backup.Id), null);
            var step = (await packages.GetAsync(package.Id))!.Steps.Single();
            Assert.Equal(backup.Id, step.Definition.CredentialId);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.DeleteAsync(backup.Id));
            Assert.Contains("Backup client", refused.Message);

            // Wieder als Lokales System: die Auswahl verfällt, und die Zugangsdaten lassen sich löschen.
            await packages.SaveStepAsync(package.Id, step.Id, Step(StepType.PowerShell, StepRunAs.System, backup.Id), null);
            Assert.Null((await packages.GetAsync(package.Id))!.Steps.Single().Definition.CredentialId);
            await credentials.DeleteAsync(backup.Id);

            await Assert.ThrowsAsync<InvalidOperationException>(() => packages.SaveStepAsync(package.Id, null, Step(StepType.PowerShell, StepRunAs.CredentialsUser, backup.Id), null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => packages.SaveStepAsync(package.Id, null, Step(StepType.FileCopy, StepRunAs.CredentialsUser, null), null));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private sealed class Recorder(TransportKind kind) : IRemoteTransport
    {
        public TransportKind Kind => kind;
        public RemoteTarget? Target { get; set; }
        public JobManifest? Job { get; set; }

        public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
        {
            (Target, Job) = (target, job);
            return Task.FromResult(new JobResult { JobId = job.JobId, Success = true });
        }
    }
}
