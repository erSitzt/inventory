using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
                result.Success = true;

                using (var progress = new StreamWriter(new FileStream(Path.Combine(_jobDir, JobFiles.Progress),
                           FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)))
                {
                    var aborted = false;
                    for (var i = 0; i < manifest.Steps.Count; i++)
                    {
                        var step = manifest.Steps[i];
                        StepResult stepResult;
                        if (aborted || _cancelled)
                        {
                            stepResult = new StepResult { Index = i, Name = step.Name, Status = StepStatus.Skipped };
                        }
                        else
                        {
                            Write(progress, new ProgressEvent { Kind = ProgressKind.StepStarted, Step = i, Text = step.Name });
                            stepResult = RunStep(i, step, progress);
                        }
                        Write(progress, new ProgressEvent { Kind = ProgressKind.StepFinished, Step = i, Status = stepResult.Status, ExitCode = stepResult.ExitCode, Text = stepResult.Error });
                        result.Steps.Add(stepResult);

                        if (stepResult.Status == StepStatus.Failed)
                        {
                            result.Success = false;
                            aborted = !step.ContinueOnError;
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
                        var ok = step.SuccessCodes == null || step.SuccessCodes.Count == 0 ? process.ExitCode == 0 : step.SuccessCodes.Contains(process.ExitCode);
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
                default:
                    throw new NotSupportedException($"Schritt-Typ {step.Type} wird von diesem Runner nicht unterstützt");
            }

            var oem = Encoding.GetEncoding((int)GetOEMCP());
            return new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = _jobDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };
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
