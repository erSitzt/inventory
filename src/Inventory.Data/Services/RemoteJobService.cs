using Inventory.Contracts;
using Inventory.Core.Abstractions;

namespace Inventory.Data.Services;

/// <summary>Führt einen Job auf einem Rechner aus und wählt dafür Transport und Zugangsdaten (Rechner-Einstellung vor Standard).</summary>
public sealed class RemoteJobService(IEnumerable<IRemoteTransport> transports, ComputerService computers, CredentialService credentials, SettingsService settings)
{
    /// <summary>Liefert immer ein Ergebnis; Zustellfehler stehen in <see cref="JobResult.Error"/>.</summary>
    public async Task<JobResult> RunAsync(int computerId, JobManifest job, IProgress<ProgressEvent>? progress = null, CancellationToken ct = default, IReadOnlyList<JobFile>? files = null)
    {
        try
        {
            var computer = await computers.GetAsync(computerId, ct);
            if (computer is null)
                return JobResult.Failed(job.JobId, $"Rechner {computerId} nicht gefunden");

            var kind = computer.Transport ?? await settings.GetDefaultTransportAsync(ct);
            var transport = transports.FirstOrDefault(t => t.Kind == kind);
            if (transport is null)
                return JobResult.Failed(job.JobId, $"Der Transport {kind} steht auf diesem Server nicht zur Verfügung");

            var credential = await credentials.ResolveAsync(computerId, ct);
            return await transport.RunAsync(new RemoteTarget(computer.Name, credential), job, files ?? [], progress, ct);
        }
        catch (RemoteException ex)
        {
            return JobResult.Failed(job.JobId, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return JobResult.Failed(job.JobId, "Abgebrochen");
        }
    }
}
