using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

namespace Inventory.Remoting;

/// <summary>Führt nichts aus, sondern liefert plausible Ergebnisse; für die Entwicklung ohne Zielrechner (z. B. unter WSL).</summary>
public sealed class SimulatedTransport(TransportKind kind) : IRemoteTransport
{
    public TransportKind Kind => kind;

    public async Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
    {
        var result = new JobResult { JobId = job.JobId, Success = true, StartedUtc = DateTime.UtcNow };
        var aborted = false;
        for (var i = 0; i < job.Steps.Count; i++)
        {
            var step = job.Steps[i];
            if (aborted)
            {
                result.Steps.Add(new StepResult { Index = i, Name = step.Name, Status = StepStatus.Skipped, Output = "Nicht ausgeführt: Ein vorheriger Schritt ist fehlgeschlagen" });
                continue;
            }

            progress?.Report(new ProgressEvent { Kind = ProgressKind.StepStarted, Step = i, Text = step.Name });
            var lines = new[]
            {
                $"[Simulation] {target.Host} über {kind} als {target.Credential?.UserName ?? "Dienstkonto"}",
                $"[Simulation] {step.Type}: {Describe(step)}",
            };
            foreach (var line in lines)
            {
                await Task.Delay(300, ct);
                progress?.Report(new ProgressEvent { Kind = ProgressKind.Output, Step = i, Text = line });
            }

            // Schritte, deren Name "fehler" enthält, schlagen fehl: so lässt sich das Fehlerverhalten ohne Zielrechner ausprobieren.
            var fails = step.Name.Contains("fehler", StringComparison.OrdinalIgnoreCase);
            var status = fails ? StepStatus.Failed : StepStatus.Succeeded;
            progress?.Report(new ProgressEvent { Kind = ProgressKind.StepFinished, Step = i, Status = status, ExitCode = fails ? 1 : 0 });
            result.Steps.Add(new StepResult
            {
                Index = i, Name = step.Name, Status = status, ExitCode = fails ? 1 : 0, Error = fails ? "Exitcode 1" : null,
                Output = string.Join(Environment.NewLine, lines) + Environment.NewLine, DurationSeconds = 0.6,
            });
            if (fails)
            {
                result.Success = false;
                aborted = !step.ContinueOnError;
            }
            else if (step.Type == StepType.InventoryScan)
            {
                result.Inventory = SimulatedInventory.Create(target.Host);
            }
        }
        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    private static string Describe(JobStep step) => step.Type switch
    {
        StepType.Install => $"{step.File} {step.Arguments}".Trim(),
        StepType.FileCopy => $"{step.File} → {step.Destination}",
        StepType.Service => $"{step.ServiceAction} {step.ServiceName}",
        StepType.Sleep or StepType.Reboot => $"{step.Seconds} s",
        _ => step.Script.Split('\n')[0].Trim(),
    };
}
