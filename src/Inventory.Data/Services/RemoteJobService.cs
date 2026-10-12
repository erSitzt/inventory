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
    /// <param name="agentless">Den Agenten umgehen, auch wenn der Rechner einen hat. Nur für die Installation des Agenten selbst: sie bleibt bei <see cref="AgentlessPolicy.AgentInstallOnly"/> erlaubt.</param>
    public async Task<JobResult> RunAsync(int computerId, JobManifest job, IProgress<ProgressEvent>? progress = null, CancellationToken ct = default,
        IReadOnlyList<JobFile>? files = null, bool agentless = false)
    {
        try
        {
            var computer = await computers.GetAsync(computerId, ct);
            if (computer is null)
                return JobResult.Failed(job.JobId, L["Computer {0} not found", computerId]);

            var kind = computer.Transport ?? await settings.GetDefaultTransportAsync(ct);
            if (agentless && kind == TransportKind.Agent)
                kind = await settings.GetDefaultTransportAsync(ct);
            if (kind != TransportKind.Agent)
            {
                // Vor jedem Zugriff prüfen: auch der Ausweichweg unten ist agentenlos und damit mit abgedeckt.
                var policy = await settings.GetAgentlessPolicyAsync(ct);
                if (policy == AgentlessPolicy.Never)
                    return JobResult.Failed(job.JobId, L["Agentless access is switched off in the settings and this computer has no agent."]);
                if (policy == AgentlessPolicy.AgentInstallOnly && !agentless)
                    return JobResult.Failed(job.JobId, L["This computer has no agent. Agentless access is only allowed for installing the agent (see settings)."]);
            }
            var transport = transports.FirstOrDefault(t => t.Kind == kind);
            if (transport is null)
                return JobResult.Failed(job.JobId, L["The transport {0} is not available on this server", kind]);

            // Reine Scans dürfen überall laufen: sie zeigen ja gerade, welcher Rechner tatsächlich antwortet.
            if (job.Steps.Any(s => s.Type != StepType.InventoryScan) && await settings.GetVerifyTargetNameAsync(ct))
                job.ExpectedComputerName = computer.ExpectedMachineName;

            var target = new RemoteTarget(computer.Name, await credentials.ResolveAsync(computerId, ct));
            try
            {
                return await transport.RunAsync(target, job, files ?? [], progress, ct);
            }
            catch (RemoteException ex)
            {
                // Zustellfehler heißt: auf dem Ziel ist noch nichts gelaufen, ein zweiter Versuch auf anderem Weg ist gefahrlos.
                var fallback = Fallback(kind);
                if (fallback is null || !await settings.GetTransportFallbackAsync(ct))
                    return JobResult.Failed(job.JobId, ex.Message);
                try
                {
                    return await fallback.RunAsync(target, job, files ?? [], progress, ct);
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
}
