using System.Net;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Führt einen Job auf einem Rechner aus und wählt dafür Transport und Zugangsdaten (Rechner-Einstellung vor Standard).</summary>
/// <param name="probe">Prüft, ob der DNS den Namen kennt; ohne (in Tests) gilt jeder Name als bekannt.</param>
public sealed class RemoteJobService(IEnumerable<IRemoteTransport> transports, ComputerService computers, CredentialService credentials, SettingsService settings,
    IStringLocalizer<SharedResource>? localizer = null, IHostProbe? probe = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    private IRemoteTransport? Fallback(TransportKind primary) => primary switch
    {
        TransportKind.SmbService => transports.FirstOrDefault(t => t.Kind == TransportKind.WinRm),
        TransportKind.WinRm => transports.FirstOrDefault(t => t.Kind == TransportKind.SmbService),
        _ => null,
    };

    /// <summary>Liefert immer ein Ergebnis; Zustellfehler stehen in <see cref="JobResult.Error"/>.</summary>
    /// <param name="credentialId">Diese Zugangsdaten statt der des Rechners (bzw. des Standards), z. B. für einen Remote-Befehl.</param>
    public Task<JobResult> RunAsync(int computerId, JobManifest job, IProgress<ProgressEvent>? progress = null, CancellationToken ct = default,
        IReadOnlyList<JobFile>? files = null, int? credentialId = null) => RunCoreAsync(computerId, job, progress, ct, files, agentInstall: false, credentialId);

    /// <summary>
    /// Installiert oder aktualisiert den Agenten. Das läuft immer agentenlos (der Agent kann sich nicht selbst ersetzen)
    /// und bleibt bei <see cref="AgentlessPolicy.AgentInstallOnly"/> erlaubt – deshalb nur für Jobs, die der <see cref="AgentService"/> gebaut hat.
    /// </summary>
    public Task<JobResult> RunAgentInstallAsync(int computerId, BuiltJob job, CancellationToken ct = default)
    {
        if (!job.InstallsAgent)
            throw new ArgumentException("Not an agent installation job.", nameof(job));
        // Jeder Rechner braucht eine eigene Job-ID.
        return RunCoreAsync(computerId, new JobManifest { Steps = job.Manifest.Steps }, null, ct, job.Files, agentInstall: true, credentialId: null);
    }

    /// <summary>Was der Mac-Agent ausführen kann.</summary>
    public static readonly IReadOnlySet<StepType> MacSteps = new HashSet<StepType> { StepType.InventoryScan, StepType.Sleep, StepType.UninstallAgent };

    private async Task<JobResult> RunCoreAsync(int computerId, JobManifest job, IProgress<ProgressEvent>? progress, CancellationToken ct,
        IReadOnlyList<JobFile>? files, bool agentInstall, int? credentialId)
    {
        try
        {
            var computer = await computers.GetAsync(computerId, ct);
            if (computer is null)
                return JobResult.Failed(job.JobId, L["Computer {0} not found", computerId]);
            // Pakete, Befehle und Windows-Aktionen gibt es für Macs noch nicht; ihr Agent kennt nur das Inventar.
            if (computer.IsMac && (agentInstall || job.Steps.Any(s => !MacSteps.Contains(s.Type))))
                return JobResult.Failed(job.JobId, L["Not available for macOS yet: on a Mac the agent collects the inventory and can be removed, nothing else."]);

            // Einen Mac erreicht nur sein Agent, gleich welcher Transport eingetragen ist.
            var kind = computer.IsMac ? TransportKind.Agent : computer.Transport ?? await settings.GetDefaultTransportAsync(ct);
            if (agentInstall && kind == TransportKind.Agent)
                kind = await settings.GetDefaultTransportAsync(ct);
            if (kind != TransportKind.Agent)
            {
                // Vor jedem Zugriff prüfen: auch der Ausweichweg unten ist agentenlos und damit mit abgedeckt.
                var policy = await settings.GetAgentlessPolicyAsync(ct);
                if (policy == AgentlessPolicy.Never)
                    return JobResult.Failed(job.JobId, L["Agentless access ({0}) is switched off in the settings. Jobs reach this computer only through an agent.", kind]);
                if (policy == AgentlessPolicy.AgentInstallOnly && !agentInstall)
                    return JobResult.Failed(job.JobId, L["Agentless access ({0}) is only allowed for installing the agent (see settings). Other jobs reach this computer only through an agent.", kind]);
            }
            var transport = transports.FirstOrDefault(t => t.Kind == kind);
            if (transport is null)
                return JobResult.Failed(job.JobId, L["The transport {0} is not available on this server", kind]);

            // Kennt der DNS den Namen nicht, geht es über die zuletzt bekannte IP-Adresse (der Agent braucht keine).
            var (host, viaKnownIp) = kind != TransportKind.Agent && probe is not null ? await probe.ConnectHostAsync(computer, ct) : (computer.Address, false);
            // Reine Scans dürfen überall laufen: sie zeigen ja gerade, welcher Rechner tatsächlich antwortet. Über eine alte
            // IP-Adresse aber nicht: Dort könnte längst ein anderer Rechner stehen, dessen Inventar dann beim falschen Eintrag landete.
            if (viaKnownIp || job.Steps.Any(s => s.Type != StepType.InventoryScan) && await settings.GetVerifyTargetNameAsync(ct))
                job.ExpectedComputerName = computer.ExpectedMachineName;

            var credential = credentialId is null ? await credentials.ResolveAsync(computerId, ct) : await credentials.GetAsync(credentialId, ct);
            if (credentialId is not null && credential is null)
                return JobResult.Failed(job.JobId, L["The chosen credentials no longer exist."]);
            if (credential is null && JobSecrets.Accounts(job).Contains(null))
                return JobResult.Failed(job.JobId, L["Running as the credentials user needs credentials: choose some, store them on the computer or set default credentials."]);
            // Schritte, die als bestimmte Zugangsdaten laufen: nur diese darf der Runner erfragen.
            var accounts = new Dictionary<int, NetworkCredential>();
            foreach (var id in JobSecrets.Accounts(job).OfType<int>())
            {
                if (await credentials.GetAsync(id, ct) is not { } account)
                    return JobResult.Failed(job.JobId, L["The step \"{0}\" runs as credentials that no longer exist. Choose others in its package.",
                        job.Steps.First(s => s.RunAs == StepRunAs.CredentialsUser && s.CredentialId == id).Name]);
                accounts[id] = account;
            }
            // Der Agent holt seine Jobs unter dem Namen des Eintrags ab; alle anderen Wege verbinden sich zum Rechner und brauchen einen Namen, der überall aufgelöst wird.
            RemoteTarget Target(IRemoteTransport via) => new(via.Kind == TransportKind.Agent ? computer.Name : host, credential, accounts.Count > 0 ? accounts : null);
            try
            {
                var target = Target(transport);
                return await transport.RunAsync(target, For(transport, job, target), files ?? [], progress, ct);
            }
            catch (RemoteException ex) when (ex.ResultMayFollow)
            {
                // Der Job lief womöglich schon: kein zweiter Versuch auf anderem Weg, sonst liefe er doppelt.
                var failed = JobResult.Failed(job.JobId, ex.Message);
                failed.ResultMayFollow = true;
                return failed;
            }
            catch (RemoteException ex)
            {
                // Zustellfehler heißt: auf dem Ziel ist noch nichts gelaufen, ein zweiter Versuch auf anderem Weg ist gefahrlos.
                var fallback = Fallback(kind);
                if (fallback is null || ex.NoFallback || !await settings.GetTransportFallbackAsync(ct))
                    return JobResult.Failed(job.JobId, ex.Message);
                try
                {
                    var target = Target(fallback);
                    return await fallback.RunAsync(target, For(fallback, job, target), files ?? [], progress, ct);
                }
                catch (RemoteException second)
                {
                    return JobResult.Failed(job.JobId, $"{ex.Message} – " + L["fallback attempt via {0}: {1}", fallback.Kind, second.Message]);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return JobResult.Failed(job.JobId, "Abgebrochen");
        }
    }

    /// <summary>
    /// Über WinRM läuft der Runner in einer Netzwerkanmeldung ohne Zugang zur Sitzung des Benutzers: dort wird niemand
    /// gefragt, das Paket startet ohne Hinweis. Und er läuft bereits als das Konto der Zugangsdaten, also gilt ein
    /// solcher Schritt dort wie einer als „System“. Gilt auch, wenn WinRM nur der Ausweichweg ist.
    /// </summary>
    /// <exception cref="RemoteException">
    /// Ein Schritt soll über WinRM als anderes Konto laufen als das der Verbindung. Dort kann sich der Runner nicht selbst
    /// als ein weiteres Konto anmelden; gestartet ist noch nichts, ein anderer Transport darf es versuchen.
    /// </exception>
    private JobManifest For(IRemoteTransport transport, JobManifest job, RemoteTarget target)
    {
        if (transport.Kind != TransportKind.WinRm)
            return job;
        job.UserPrompt = null;
        if (!JobSecrets.Needed(job))
            return job;
        // Eine Kopie: Dasselbe Manifest kann noch an andere Rechner gehen, die über SMB oder den Agenten laufen.
        var copy = JobJson.Deserialize<JobManifest>(JobJson.Serialize(job))!;
        foreach (var step in copy.Steps.Where(s => s.RunAs == StepRunAs.CredentialsUser))
        {
            if (!SameAccount(target.RunAsAccount(step.CredentialId), target.Credential))
                throw new RemoteException(L["The step \"{0}\" runs as {1}. Over WinRM it can only run as the account that connects ({2}); use SMB + service or the agent.",
                    step.Name, target.RunAsAccount(step.CredentialId) is { } account ? JobSecrets.Account(account) : "?",
                    target.Credential is { } connection ? JobSecrets.Account(connection) : L["the server's account"]]);
            step.RunAs = StepRunAs.System;
            step.CredentialId = null;
        }
        return copy;
    }

    /// <summary>Dasselbe Windows-Konto, auch wenn es zweimal hinterlegt ist: Der Runner läuft über WinRM ohnehin als das der Verbindung.</summary>
    private static bool SameAccount(NetworkCredential? a, NetworkCredential? b) =>
        a is not null && b is not null && string.Equals(JobSecrets.Account(a), JobSecrets.Account(b), StringComparison.OrdinalIgnoreCase);
}
