using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Inventory.Contracts;

namespace Inventory.Runner
{
    /// <summary>Führt die Schritte eines Job-Manifests nacheinander aus und schreibt Fortschritt und Ergebnis ins Job-Verzeichnis.</summary>
    internal sealed class JobExecutor
    {
        private const int MaxOutputChars = 1_000_000;

        private readonly string _jobDir;
        private readonly object _sync = new object();
        private Process? _current;
        private volatile bool _cancelled;

        public JobExecutor(string jobDir) => _jobDir = jobDir;

        public JobResult Run()
        {
            var result = new JobResult { StartedUtc = DateTime.UtcNow };
            try
            {
                var manifest = JobJson.Deserialize<JobManifest>(File.ReadAllText(Path.Combine(_jobDir, JobFiles.Manifest), Encoding.UTF8))
                               ?? throw new InvalidDataException("Leeres Job-Manifest");
                result.JobId = manifest.JobId;
                if (!string.IsNullOrEmpty(manifest.ExpectedComputerName)
                    && !string.Equals(manifest.ExpectedComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Dieser Rechner heißt {Environment.MachineName}, erwartet wurde {manifest.ExpectedComputerName}. " +
                        "Es wurde nichts ausgeführt. Vermutlich zeigt ein veralteter DNS-Eintrag oder ein Alias auf diesen Rechner.");
                }
                result.Success = true;

                using (var progress = new StreamWriter(new FileStream(Path.Combine(_jobDir, JobFiles.Progress),
                           FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)))
                {
                    string? aborted = null;
                    for (var i = 0; i < manifest.Steps.Count; i++)
                    {
                        var step = manifest.Steps[i];
                        StepResult stepResult;
                        if (aborted != null || _cancelled)
                        {
                            stepResult = new StepResult { Index = i, Name = step.Name, Status = StepStatus.Skipped, Output = aborted ?? "Abgebrochen" };
                        }
                        else if (ConditionEvaluator.WhyNot(step.Conditions) is string reason)
                        {
                            stepResult = new StepResult { Index = i, Name = step.Name, Status = StepStatus.Skipped, Output = "Bedingung nicht erfüllt: " + reason };
                        }
                        else
                        {
                            Write(progress, new ProgressEvent { Kind = ProgressKind.StepStarted, Step = i, Text = step.Name });
                            stepResult = Dispatch(i, step, progress, result);
                            if (step.Type == StepType.Reboot && stepResult.Status == StepStatus.Succeeded)
                                aborted = "Nicht ausgeführt: Der Rechner startet neu";
                        }
                        Write(progress, new ProgressEvent { Kind = ProgressKind.StepFinished, Step = i, Status = stepResult.Status, ExitCode = stepResult.ExitCode, Text = stepResult.Error });
                        result.Steps.Add(stepResult);

                        if (stepResult.Status == StepStatus.Failed)
                        {
                            result.Success = false;
                            if (!step.ContinueOnError)
                                aborted = "Nicht ausgeführt: Ein vorheriger Schritt ist fehlgeschlagen";
                        }
                    }
                }
                if (_cancelled)
                {
                    result.Success = false;
                    result.Error = "Abgebrochen";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            result.FinishedUtc = DateTime.UtcNow;
            WriteResult(result);
            return result;
        }

        /// <summary>Bricht den laufenden Schritt ab; die restlichen werden übersprungen.</summary>
        public void Cancel()
        {
            _cancelled = true;
            Process? process;
            lock (_sync)
                process = _current;
            if (process != null)
                KillTree(process);
        }

        private string FilesDir => Path.Combine(_jobDir, JobFiles.FilesFolder);

        private StepResult Dispatch(int index, JobStep step, StreamWriter progress, JobResult job)
        {
            switch (step.Type)
            {
                case StepType.Command:
                case StepType.PowerShell:
                case StepType.Install:
                case StepType.Reboot:
                    return RunStep(index, step, progress);
                case StepType.InventoryScan:
                    return RunInProcess(index, step, progress, () =>
                    {
                        job.Inventory = InventoryCollector.Collect();
                        return string.Join(Environment.NewLine, job.Inventory.Warnings);
                    });
                case StepType.FileCopy:
                    return RunInProcess(index, step, progress, () => CopyFiles(step));
                case StepType.Service:
                    return RunInProcess(index, step, progress, () => ControlService(step));
                case StepType.Sleep:
                    return RunInProcess(index, step, progress, () =>
                    {
                        for (var waited = 0; waited < step.Seconds * 10 && !_cancelled; waited++)
                            Thread.Sleep(100);
                        return $"{step.Seconds} s gewartet";
                    });
                default:
                    return new StepResult { Index = index, Name = step.Name, Status = StepStatus.Failed, Error = $"Schritt-Typ {step.Type} wird von diesem Runner nicht unterstützt" };
            }
        }

        /// <summary>Schritte ohne Kindprozess; die Rückgabe der Aktion wird zur Ausgabe des Schritts.</summary>
        private static StepResult RunInProcess(int index, JobStep step, StreamWriter progress, Func<string> action)
        {
            var result = new StepResult { Index = index, Name = step.Name };
            var watch = Stopwatch.StartNew();
            try
            {
                result.Output = action();
                result.Status = StepStatus.Succeeded;
                foreach (var line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    Write(progress, new ProgressEvent { Kind = ProgressKind.Output, Step = index, Text = line });
            }
            catch (Exception ex)
            {
                result.Status = StepStatus.Failed;
                result.Error = ex.Message;
            }
            result.DurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
            return result;
        }

        private string CopyFiles(JobStep step)
        {
            if (string.IsNullOrWhiteSpace(step.File) || string.IsNullOrWhiteSpace(step.Destination))
                throw new InvalidDataException("Quelle oder Ziel fehlt");
            var source = Path.Combine(FilesDir, step.File!);
            var destination = Environment.ExpandEnvironmentVariables(step.Destination!);

            if (File.Exists(source))
            {
                // Endet das Ziel auf einen Trenner oder ist es ein vorhandener Ordner, bleibt der Dateiname erhalten.
                if (destination.EndsWith("\\", StringComparison.Ordinal) || Directory.Exists(destination))
                    destination = Path.Combine(destination, Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                return $"1 Datei nach {destination} kopiert";
            }
            if (Directory.Exists(source))
            {
                var count = 0;
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(destination, file.Substring(source.Length).TrimStart('\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                    count++;
                }
                return $"{count} Dateien nach {destination} kopiert";
            }
            throw new FileNotFoundException($"Paketdatei {step.File} nicht gefunden");
        }

        private static string ControlService(JobStep step)
        {
            if (string.IsNullOrWhiteSpace(step.ServiceName))
                throw new InvalidDataException("Dienstname fehlt");
            var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds > 0 ? step.TimeoutSeconds : 60);
            using (var service = new ServiceController(step.ServiceName))
            {
                if (step.ServiceAction != ServiceAction.Start && service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                }
                if (step.ServiceAction != ServiceAction.Stop && service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, timeout);
                }
                service.Refresh();
                return $"Dienst {step.ServiceName}: {service.Status}";
            }
        }

        private StepResult RunStep(int index, JobStep step, StreamWriter progress)
        {
            var result = new StepResult { Index = index, Name = step.Name };
            var watch = Stopwatch.StartNew();
            try
            {
                var startInfo = CreateStartInfo(index, step);
                var output = new StringBuilder();
                using (var process = new Process { StartInfo = startInfo })
                {
                    DataReceivedEventHandler onLine = (_, e) =>
                    {
                        if (e.Data == null)
                            return;
                        lock (_sync)
                        {
                            if (output.Length < MaxOutputChars)
                                output.AppendLine(e.Data);
                            Write(progress, new ProgressEvent { Kind = ProgressKind.Output, Step = index, Text = e.Data });
                        }
                    };
                    process.OutputDataReceived += onLine;
                    process.ErrorDataReceived += onLine;

                    process.Start();
                    lock (_sync)
                        _current = process;
                    process.StandardInput.Close();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var timeout = step.TimeoutSeconds > 0 ? step.TimeoutSeconds * 1000 : -1;
                    if (!process.WaitForExit(timeout))
                    {
                        KillTree(process);
                        process.WaitForExit(10_000);
                        result.Status = StepStatus.Failed;
                        result.Error = $"Zeitüberschreitung nach {step.TimeoutSeconds} s";
                    }
                    else
                    {
                        // Erst der parameterlose Aufruf wartet, bis auch die Ausgabe vollständig gelesen ist.
                        process.WaitForExit();
                        result.ExitCode = process.ExitCode;
                        var ok = SuccessCodes(step).Contains(process.ExitCode);
                        result.Status = ok ? StepStatus.Succeeded : StepStatus.Failed;
                        if (!ok)
                            result.Error = _cancelled ? "Abgebrochen" : $"Exitcode {process.ExitCode}";
                    }
                    lock (_sync)
                    {
                        _current = null;
                        result.Output = output.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                result.Status = StepStatus.Failed;
                result.Error = ex.Message;
            }
            result.DurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
            return result;
        }

        private ProcessStartInfo CreateStartInfo(int index, JobStep step)
        {
            var system = Environment.SystemDirectory;
            string fileName, arguments;
            switch (step.Type)
            {
                case StepType.Command:
                    fileName = Path.Combine(system, "cmd.exe");
                    arguments = "/d /s /c \"" + step.Script + "\"";
                    break;
                case StepType.PowerShell:
                    var scriptPath = Path.Combine(_jobDir, $"step-{index}.ps1");
                    // BOM, damit Windows PowerShell 5.1 Umlaute im Skript korrekt liest.
                    File.WriteAllText(scriptPath, step.Script, new UTF8Encoding(true));
                    fileName = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
                    arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
                    break;
                case StepType.Install:
                    CreateInstallCommand(step, out fileName, out arguments);
                    break;
                case StepType.Reboot:
                    fileName = Path.Combine(system, "shutdown.exe");
                    arguments = $"/r /t {Math.Max(step.Seconds, 0)} /d p:4:1";
                    if (!string.IsNullOrWhiteSpace(step.Script))
                        arguments += " /c \"" + step.Script.Replace("\"", "'") + "\"";
                    break;
                default:
                    throw new NotSupportedException($"Schritt-Typ {step.Type} wird von diesem Runner nicht unterstützt");
            }

            var oem = Encoding.GetEncoding((int)GetOEMCP());
            return new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = Directory.Exists(FilesDir) ? FilesDir : _jobDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };
        }

        private void CreateInstallCommand(JobStep step, out string fileName, out string arguments)
        {
            if (string.IsNullOrWhiteSpace(step.File))
                throw new InvalidDataException("Keine Installationsdatei angegeben");
            var file = Path.Combine(FilesDir, step.File!);
            if (!File.Exists(file))
                throw new FileNotFoundException($"Paketdatei {step.File} nicht gefunden");

            var system = Environment.SystemDirectory;
            var extra = string.IsNullOrWhiteSpace(step.Arguments) ? "" : " " + step.Arguments!.Trim();
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".msi":
                    fileName = Path.Combine(system, "msiexec.exe");
                    arguments = $"/i \"{file}\" /qn /norestart{extra}";
                    break;
                case ".msp":
                    fileName = Path.Combine(system, "msiexec.exe");
                    arguments = $"/p \"{file}\" /qn /norestart{extra}";
                    break;
                case ".msu":
                    fileName = Path.Combine(system, "wusa.exe");
                    arguments = $"\"{file}\" /quiet /norestart{extra}";
                    break;
                case ".cmd":
                case ".bat":
                    fileName = Path.Combine(system, "cmd.exe");
                    arguments = $"/d /s /c \"\"{file}\"{extra}\"";
                    break;
                case ".ps1":
                    fileName = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
                    arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"{extra}";
                    break;
                default:
                    fileName = file;
                    arguments = extra.TrimStart();
                    break;
            }
        }

        private static List<int> SuccessCodes(JobStep step)
        {
            if (step.SuccessCodes != null && step.SuccessCodes.Count > 0)
                return step.SuccessCodes;
            // 3010 = Neustart erforderlich, 1641 = Neustart eingeleitet: beides erfolgreiche Installationen.
            return step.Type == StepType.Install ? new List<int> { 0, 3010, 1641 } : new List<int> { 0 };
        }

        private static void Write(StreamWriter progress, ProgressEvent e)
        {
            lock (progress)
            {
                progress.WriteLine(JobJson.Serialize(e));
                progress.Flush();
            }
        }

        private void WriteResult(JobResult result)
        {
            // Erst temporär schreiben und dann umbenennen: der Server darf nie eine halbe Datei sehen.
            var path = Path.Combine(_jobDir, JobFiles.Result);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JobJson.Serialize(result, indented: true), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private static void KillTree(Process process)
        {
            try
            {
                // .NET Framework kennt kein Kill(entireProcessTree).
                using (var kill = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), $"/T /F /PID {process.Id}")
                       {
                           UseShellExecute = false,
                           CreateNoWindow = true,
                       }))
                {
                    kill?.WaitForExit(10_000);
                }
            }
            catch (Exception)
            {
                try { process.Kill(); } catch (Exception) { /* Prozess ist bereits beendet */ }
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }
}
