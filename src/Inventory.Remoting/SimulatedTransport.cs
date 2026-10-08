using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

namespace Inventory.Remoting;

/// <summary>Führt nichts aus, sondern liefert plausible Ergebnisse; für die Entwicklung ohne Zielrechner (z. B. unter WSL).</summary>
public sealed class SimulatedTransport(TransportKind kind) : IRemoteTransport
{
    public TransportKind Kind => kind;

    public async Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IProgress<ProgressEvent>? progress, CancellationToken ct)
    {
        var result = new JobResult { JobId = job.JobId, Success = true, StartedUtc = DateTime.UtcNow };
        for (var i = 0; i < job.Steps.Count; i++)
        {
            var step = job.Steps[i];
            progress?.Report(new ProgressEvent { Kind = ProgressKind.StepStarted, Step = i, Text = step.Name });
            var lines = new[]
            {
                $"[Simulation] {target.Host} über {kind} als {target.Credential?.UserName ?? "Dienstkonto"}",
                $"[Simulation] {step.Type}: {step.Script.Split('\n')[0].Trim()}",
            };
            foreach (var line in lines)
            {
                await Task.Delay(300, ct);
                progress?.Report(new ProgressEvent { Kind = ProgressKind.Output, Step = i, Text = line });
            }
            progress?.Report(new ProgressEvent { Kind = ProgressKind.StepFinished, Step = i, Status = StepStatus.Succeeded, ExitCode = 0 });
            result.Steps.Add(new StepResult
            {
                Index = i, Name = step.Name, Status = StepStatus.Succeeded, ExitCode = 0,
                Output = string.Join(Environment.NewLine, lines) + Environment.NewLine, DurationSeconds = 0.6,
            });
        }
        if (job.Steps.Any(s => s.Type == StepType.InventoryScan))
            result.Inventory = SimulatedInventory.Create(target.Host);
        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }
}
