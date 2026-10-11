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
        var result = new JobResult { JobId = job.JobId, Success = true, StartedUtc = DateTime.UtcNow, BootTimeUtc = SimulatedBootTime(job) };
        var aborted = false;
        // Erkennung gegen das simulierte Inventar: so lässt sich "bereits installiert" ohne Zielrechner ausprobieren.
        result.AlreadyInstalled = job.SkipIfDetected && Detected(job.Detection, target.Host);
        for (var i = 0; i < job.Steps.Count; i++)
        {
            var step = job.Steps[i];
            if (result.ResumeFromStep != null)
            {
                result.Steps.Add(new StepResult { Index = i, Name = step.Name, Status = StepStatus.Deferred, Output = "Continues after the restart" });
                continue;
            }
            if (result.AlreadyInstalled)
            {
                result.Steps.Add(new StepResult { Index = i, Name = step.Name, Status = StepStatus.Skipped, Output = "Already installed" });
                continue;
            }
            if (aborted)
            {
                result.Steps.Add(new StepResult { Index = i, Name = step.Name, Status = StepStatus.Skipped, Output = "Not executed: a previous step failed" });
                continue;
            }

            progress?.Report(new ProgressEvent { Kind = ProgressKind.StepStarted, Step = i, Text = step.Name });
            var lines = new[]
            {
                $"[Simulation] {target.Host} via {kind} as {target.Credential?.UserName ?? "service account"}",
                $"[Simulation] {step.Type}{(step.RunAs == StepRunAs.LoggedOnUser ? " as logged-on user" : "")}: {Describe(step)}",
            };
            foreach (var line in lines)
            {
                await Task.Delay(300, ct);
                progress?.Report(new ProgressEvent { Kind = ProgressKind.Output, Step = i, Text = line });
            }

            // Schritte, deren Name "fail" oder "fehler" enthält, schlagen fehl: so lässt sich das Fehlerverhalten ohne Zielrechner ausprobieren.
            var fails = step.Name.Contains("fail", StringComparison.OrdinalIgnoreCase) || step.Name.Contains("fehler", StringComparison.OrdinalIgnoreCase);
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
            else if (step.Type == StepType.Reboot && i + 1 < job.Steps.Count)
            {
                result.ResumeFromStep = i + 1;
            }
        }
        // Die Simulation installiert nichts; die Prüfung schlägt nur fehl, wenn der gesuchte Name "fail" oder "fehler" enthält.
        if (result.Success && !result.AlreadyInstalled && result.ResumeFromStep is null && job.VerifyAfterInstall
            && job.Detection?.SoftwareInstalled is { } wanted && (wanted.Contains("fail", StringComparison.OrdinalIgnoreCase) || wanted.Contains("fehler", StringComparison.OrdinalIgnoreCase)))
        {
            result.Success = false;
            result.Error = $"Verification failed: {wanted} is not installed";
        }
        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>Die Simulation "startet" sofort neu: eine Fortsetzung sieht immer eine jüngere Startzeit als zuvor.</summary>
    private static DateTime SimulatedBootTime(JobManifest job) =>
        job.RequireBootAfterUtc is { } previous && previous > DateTime.UtcNow.AddHours(-1) ? previous.AddMinutes(1) : DateTime.UtcNow.AddHours(-1);

    private static bool Detected(StepConditions? detection, string host) =>
        detection is { IsEmpty: false } && !string.IsNullOrWhiteSpace(detection.SoftwareInstalled)
        && SimulatedInventory.Create(host).Software.Any(s => SoftwareMatcher.Matches(detection.SoftwareInstalled, detection.SoftwareMinVersion, s.Name, s.Version));

    private static string Describe(JobStep step) => step.Type switch
    {
        StepType.Install => $"{step.File} {step.Arguments}".Trim(),
        StepType.FileCopy => $"{step.File} → {step.Destination}",
        StepType.Service => $"{step.ServiceAction} {step.ServiceName}",
        StepType.Uninstall => $"{step.ProductCode ?? step.SoftwareName} {step.Arguments}".Trim(),
        StepType.Sleep or StepType.Reboot => $"{step.Seconds} s",
        _ => step.Script.Split('\n')[0].Trim(),
    };
}
