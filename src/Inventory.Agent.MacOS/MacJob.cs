using System.Text;
using Inventory.Contracts;

namespace Inventory.Agent.MacOS;

/// <summary>
/// Führt einen Job auf dem Mac aus. Bisher nur das Inventar, Warten und das Entfernen des Agenten; alle anderen Schritte sind
/// Windows-Schritte und scheitern mit einer klaren Meldung, ohne dass etwas ausgeführt wird.
/// </summary>
/// <param name="agentDirectory">Der Ordner des Agenten; ihn räumt "Agent entfernen" ab.</param>
internal sealed class MacJob(string jobDir, string hostName, string agentDirectory) : IAgentJob
{
    private volatile bool _cancelled;

    public JobResult Run()
    {
        var result = new JobResult { StartedUtc = DateTime.UtcNow, BootTimeUtc = MacInventoryCollector.BootTimeUtc() };
        try
        {
            var manifest = JobJson.Deserialize<JobManifest>(File.ReadAllText(Path.Combine(jobDir, JobFiles.Manifest), Encoding.UTF8))
                           ?? throw new InvalidDataException("Empty job manifest");
            result.JobId = manifest.JobId;
            if (!string.IsNullOrEmpty(manifest.ExpectedComputerName) && !IsThisComputer(manifest.ExpectedComputerName.Split('.')[0]))
            {
                throw new InvalidOperationException(
                    $"This computer is {hostName}, expected was {manifest.ExpectedComputerName}. Nothing was run.");
            }

            result.Success = true;
            for (var i = 0; i < manifest.Steps.Count; i++)
            {
                var step = manifest.Steps[i];
                if (_cancelled || !result.Success)
                {
                    result.Steps.Add(new StepResult { Index = i, Name = step.Name, Status = StepStatus.Skipped });
                    continue;
                }
                Write(new ProgressEvent { Kind = ProgressKind.StepStarted, Step = i, Text = step.Name });
                var started = DateTime.UtcNow;
                var stepResult = Execute(i, step, result);
                stepResult.DurationSeconds = (DateTime.UtcNow - started).TotalSeconds;
                result.Steps.Add(stepResult);
                if (stepResult.Status == StepStatus.Failed)
                {
                    result.Success = false;
                    result.Error ??= stepResult.Error;
                }
                Write(new ProgressEvent { Kind = ProgressKind.StepFinished, Step = i, Status = stepResult.Status, Text = stepResult.Error });
            }
            if (_cancelled && result.Success)
            {
                result.Success = false;
                result.Error = "Cancelled";
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>Der eigene Name, auch in der auf 15 Zeichen gekürzten Form, wie ältere Server sie für Windows erwarten.</summary>
    private bool IsThisComputer(string expected) =>
        string.Equals(expected, hostName, StringComparison.OrdinalIgnoreCase)
        || (hostName.Length > 15 && string.Equals(expected, hostName[..15], StringComparison.OrdinalIgnoreCase));

    private StepResult Execute(int index, JobStep step, JobResult result)
    {
        var stepResult = new StepResult { Index = index, Name = step.Name, Status = StepStatus.Succeeded };
        switch (step.Type)
        {
            case StepType.InventoryScan:
                try
                {
                    result.Inventory = new MacInventoryCollector(hostName).Collect();
                    stepResult.Output = result.Inventory.Warnings.Count == 0 ? "" : string.Join("\n", result.Inventory.Warnings);
                }
                catch (Exception ex)
                {
                    // Ein unvollständiges Inventar würde den gespeicherten Stand überschreiben: lieber kein Ergebnis.
                    stepResult.Status = StepStatus.Failed;
                    stepResult.Error = "Inventory scan failed: " + ex.Message;
                }
                break;
            case StepType.UninstallAgent:
                stepResult.Output = MacAgentRemoval.Schedule(agentDirectory, result.JobId);
                break;
            case StepType.Sleep:
                for (var waited = 0; waited < step.Seconds && !_cancelled; waited++)
                    Thread.Sleep(1000);
                break;
            default:
                stepResult.Status = StepStatus.Failed;
                stepResult.Error = $"Step type {step.Type} is not supported on macOS yet.";
                break;
        }
        return stepResult;
    }

    private void Write(ProgressEvent e)
    {
        try
        {
            File.AppendAllText(Path.Combine(jobDir, JobFiles.Progress), JobJson.Serialize(e).Replace("\n", " ") + "\n", new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Fortschritt ist nur Komfort.
        }
    }

    public void Cancel()
    {
        _cancelled = true;
        Shell.KillCurrent();
    }
}
