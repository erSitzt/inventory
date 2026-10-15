using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Führt einen Job auf einem Rechner aus und wählt dafür Transport und Zugangsdaten (Rechner-Einstellung vor Standard).</summary>
public sealed class RemoteJobService(IEnumerable<IRemoteTransport> transports, ComputerService computers, CredentialService credentials, SettingsService settings, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    private IRemoteTransport? Fallback(TransportKind primary) => primary switch
    {
        TransportKind.SmbService => transports.FirstOrDefault(t => t.Kind == TransportKind.WinRm),
        TransportKind.WinRm => transports.FirstOrDefault(t => t.Kind == TransportKind.SmbService),
        _ => null,
    };

    /// <summary>Liefert immer ein Ergebnis; Zustellfehler stehen in <see cref="JobResult.Error"/>.</summary>
    public Task<JobResult> RunAsync(int computerId, JobManifest job, IProgress<ProgressEvent>? progress = null, CancellationToken ct = default,
        IReadOnlyList<JobFile>? files = null) => RunCoreAsync(computerId, job, progress, ct, files, agentInstall: false);

    /// <summary>
    /// Installiert oder aktualisiert den Agenten. Das läuft immer agentenlos (der Agent kann sich nicht selbst ersetzen)
    /// und bleibt bei <see cref="AgentlessPolicy.AgentInstallOnly"/> erlaubt – deshalb nur für Jobs, die der <see cref="AgentService"/> gebaut hat.
    /// </summary>
    public Task<JobResult> RunAgentInstallAsync(int computerId, BuiltJob job, CancellationToken ct = default)
    {
        if (!job.InstallsAgent)
            throw new ArgumentException("Not an agent installation job.", nameof(job));
        // Jeder Rechner braucht eine eigene Job-ID.
        return RunCoreAsync(computerId, new JobManifest { Steps = job.Manifest.Steps }, null, ct, job.Files, agentInstall: true);
    }

    private async Task<JobResult> RunCoreAsync(int computerId, JobManifest job, IProgress<ProgressEvent>? progress, CancellationToken ct,
        IReadOnlyList<JobFile>? files, bool agentInstall)
    {
        try
        {
            var computer = await computers.GetAsync(computerId, ct);
            if (computer is null)
                return JobResult.Failed(job.JobId, L["Computer {0} not found", computerId]);

            var kind = computer.Transport ?? await settings.GetDefaultTransportAsync(ct);
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

            // Reine Scans dürfen überall laufen: sie zeigen ja gerade, welcher Rechner tatsächlich antwortet.
            if (job.Steps.Any(s => s.Type != StepType.InventoryScan) && await settings.GetVerifyTargetNameAsync(ct))
                job.ExpectedComputerName = computer.ExpectedMachineName;

            var credential = await credentials.ResolveAsync(computerId, ct);
            // Der Agent holt seine Jobs unter dem Namen des Eintrags ab; alle anderen Wege verbinden sich zum Rechner und brauchen einen Namen, der überall aufgelöst wird.
            RemoteTarget Target(IRemoteTransport via) => new(via.Kind == TransportKind.Agent ? computer.Name : computer.Address, credential);
            try
            {
                return await transport.RunAsync(Target(transport), For(transport, job), files ?? [], progress, ct);
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
                if (fallback is null || !await settings.GetTransportFallbackAsync(ct))
                    return JobResult.Failed(job.JobId, ex.Message);
                try
                {
                    return await fallback.RunAsync(Target(fallback), For(fallback, job), files ?? [], progress, ct);
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
    /// gefragt, das Paket startet ohne Hinweis. Gilt auch, wenn WinRM nur der Ausweichweg ist.
    /// </summary>
    private static JobManifest For(IRemoteTransport transport, JobManifest job)
    {
        if (transport.Kind == TransportKind.WinRm)
            job.UserPrompt = null;
        return job;
    }
}
