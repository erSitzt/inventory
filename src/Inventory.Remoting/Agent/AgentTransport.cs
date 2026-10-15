using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Remoting.Agent;

/// <summary>
/// Zustellung über den fest installierten Agenten: der Job wird bereitgelegt, der Agent holt ihn über seine
/// ausgehende Verbindung ab. Funktioniert damit auch für Rechner, die der Server nicht direkt erreicht.
/// </summary>
public sealed class AgentTransport(AgentHub hub, IStringLocalizer<SharedResource>? localizer = null) : IRemoteTransport
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    /// <summary>Der Agent fragt im Normalfall ununterbrochen nach; meldet er sich so lange nicht, ist er offline.</summary>
    public TimeSpan PickupTimeout { get; init; } = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(10);

    public TransportKind Kind => TransportKind.Agent;

    public async Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
    {
        var pending = hub.Enqueue(target.Host, job, files, progress);
        try
        {
            try
            {
                await pending.PickedUp.Task.WaitAsync(PickupTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new RemoteException(L["The agent on {0} did not pick up the job – the computer is off or the agent is not running.", target.Host]);
            }

            // Die Schritte haben eigene Zeitlimits; die Obergrenze fängt nur einen mitten im Job verschwundenen Agenten ab.
            var limit = TimeSpan.FromSeconds(job.Steps.Sum(s => (long)Math.Max(s.TimeoutSeconds, 60))) + Slack;
            try
            {
                return await pending.Completion.Task.WaitAsync(limit, ct);
            }
            catch (TimeoutException)
            {
                // Ein Notebook, das mitten im Job das Netz verlässt (etwa durch einen Neustart), meldet sein Ergebnis später nach.
                throw new RemoteException(L["The agent on {0} returned no result (connection lost during the job?).", target.Host], resultMayFollow: true);
            }
        }
        finally
        {
            hub.Withdraw(pending);
        }
    }
}
