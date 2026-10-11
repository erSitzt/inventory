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
                result.BootTimeUtc = BootTimeUtc();
                // Fortsetzung nach einem Neustart: Dieselbe Uhr wie im vorigen Ergebnis, daher unabhängig von Zeitabweichungen zum Server.
                if (manifest.RequireBootAfterUtc is DateTime previousBoot && result.BootTimeUtc <= previousBoot.AddSeconds(5))
                {
                    result.Success = false;
                    result.NotRebootedYet = true;
                    result.Error = "The computer has not restarted yet; the remaining steps follow after the restart.";
                    manifest.Steps.Clear();
                }
                else
                {
                    result.Success = true;
                }

                using (var progress = new StreamWriter(new FileStream(Path.Combine(_jobDir, JobFiles.Progress),
                           FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)))
                {
                    string? aborted = null;
                    for (var i = 0; i < manifest.Steps.Count; i++)
                    {
                        var step = manifest.Steps[i];
                        StepResult stepResult;
                        if (result.ResumeFromStep != null)
                        {
                            stepResult = new StepResult { Index = i, Name = step.Name, Status = StepStatus.Deferred, Output = "Continues after the restart" };
                        }
                        else if (aborted != null || _cancelled)
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
                            // Die restlichen Schritte übernimmt der Server in einem Folgejob, sobald der Rechner wieder da ist.
                            if (step.Type == StepType.Reboot && stepResult.Status == StepStatus.Succeeded && i + 1 < manifest.Steps.Count)
                                result.ResumeFromStep = i + 1;
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

            if (_userDir != null)
            {
                try { Directory.Delete(_userDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
            if (_currentPid != 0)
                KillTree(_currentPid);
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
            if (step.RunAs == StepRunAs.LoggedOnUser && step.Type != StepType.Reboot)
                return RunStepAsUser(index, step, progress);

            var result = new StepResult { Index = index, Name = step.Name };
            var watch = Stopwatch.StartNew();
            try
            {
                var startInfo = CreateStartInfo(index, step, userContext: false);
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

        /// <summary>Führt den Schritt in der Sitzung des angemeldeten Benutzers aus; ohne Anmeldung wird er übersprungen.</summary>
        private StepResult RunStepAsUser(int index, JobStep step, StreamWriter progress)
        {
            var result = new StepResult { Index = index, Name = step.Name };
            var watch = Stopwatch.StartNew();
            try
            {
                using (var session = UserSession.Open())
                {
                    if (session == null)
                    {
                        result.Status = StepStatus.Skipped;
                        result.Output = "Kein Benutzer angemeldet; der Schritt läuft nur im Kontext eines angemeldeten Benutzers.";
                        return result;
                    }

                    var startInfo = CreateStartInfo(index, step, userContext: true);
                    var output = new StringBuilder();
                    Action<string> onLine = line =>
                    {
                        lock (_sync)
                        {
                            if (output.Length < MaxOutputChars)
                                output.AppendLine(line);
                            Write(progress, new ProgressEvent { Kind = ProgressKind.Output, Step = index, Text = line });
                        }
                    };
                    onLine($"[Läuft als {session.UserName}]");

                    var timeout = step.TimeoutSeconds > 0 ? step.TimeoutSeconds * 1000 : -1;
                    var exitCode = session.Run(startInfo.FileName, startInfo.Arguments, startInfo.WorkingDirectory, startInfo.StandardOutputEncoding,
                        onLine, timeout, pid => _currentPid = pid);
                    if (exitCode == null)
                    {
                        KillTree(_currentPid);
                        result.Status = StepStatus.Failed;
                        result.Error = $"Zeitüberschreitung nach {step.TimeoutSeconds} s";
                    }
                    else
                    {
                        result.ExitCode = exitCode;
                        var ok = SuccessCodes(step).Contains(exitCode.Value);
                        result.Status = ok ? StepStatus.Succeeded : StepStatus.Failed;
                        if (!ok)
                            result.Error = _cancelled ? "Abgebrochen" : $"Exitcode {exitCode}";
                    }
                    lock (_sync)
                        result.Output = output.ToString();
                }
            }
            catch (Exception ex)
            {
                result.Status = StepStatus.Failed;
                result.Error = ex.Message;
            }
            finally
            {
                _currentPid = 0;
                result.DurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
            }
            return result;
        }

        private volatile int _currentPid;
        private string? _userDir;

        /// <summary>
        /// Arbeitsordner für Schritte im Benutzerkontext. Das Job-Verzeichnis selbst ist nur für SYSTEM und Administratoren
        /// lesbar; ein normaler Benutzer käme weder an sein Skript noch an die Paketdateien. Unter ProgramData dürfen Benutzer lesen.
        /// </summary>
        private string UserDir()
        {
            if (_userDir != null)
                return _userDir;
            // Eigener Ordner neben (nicht unter) dem Datenverzeichnis des Servers: dessen Rechte sind bewusst auf SYSTEM und Administratoren beschränkt.
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InventoryRunner", "UserJobs", Path.GetFileName(_jobDir));
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (UnauthorizedAccessException)
            {
                // Läuft der Runner selbst nur mit Benutzerrechten, genügt dessen Temp-Ordner.
                dir = Path.Combine(Path.GetTempPath(), "InventoryRunner", Path.GetFileName(_jobDir));
                Directory.CreateDirectory(dir);
            }
            if (Directory.Exists(FilesDir))
            {
                foreach (var file in Directory.GetFiles(FilesDir, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(dir, JobFiles.FilesFolder, file.Substring(FilesDir.Length).TrimStart('\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }
            return _userDir = dir;
        }

        private ProcessStartInfo CreateStartInfo(int index, JobStep step, bool userContext)
        {
            var baseDir = userContext ? UserDir() : _jobDir;
            var filesDir = Path.Combine(baseDir, JobFiles.FilesFolder);
            var system = Environment.SystemDirectory;
            string fileName, arguments;
            switch (step.Type)
            {
                case StepType.Command:
                    fileName = Path.Combine(system, "cmd.exe");
                    arguments = "/d /s /c \"" + step.Script + "\"";
                    break;
                case StepType.PowerShell:
                    var scriptPath = Path.Combine(baseDir, $"step-{index}.ps1");
                    // BOM, damit Windows PowerShell 5.1 Umlaute im Skript korrekt liest.
                    File.WriteAllText(scriptPath, step.Script, new UTF8Encoding(true));
                    fileName = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
                    arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
                    break;
                case StepType.Install:
                    CreateInstallCommand(step, filesDir, out fileName, out arguments);
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
                WorkingDirectory = Directory.Exists(filesDir) ? filesDir : baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };
        }

        private static void CreateInstallCommand(JobStep step, string filesDir, out string fileName, out string arguments)
        {
            if (string.IsNullOrWhiteSpace(step.File))
                throw new InvalidDataException("Keine Installationsdatei angegeben");
            var file = Path.Combine(filesDir, step.File!);
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
                KillTree(process.Id);
            }
            catch (Exception)
            {
                try { process.Kill(); } catch (Exception) { /* Prozess ist bereits beendet */ }
            }
        }

        private static void KillTree(int processId)
        {
            if (processId == 0)
                return;
            // .NET Framework kennt kein Kill(entireProcessTree).
            using (var kill = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), $"/T /F /PID {processId}")
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true,
                   }))
            {
                kill?.WaitForExit(10_000);
            }
        }

        /// <summary>Startzeit des Betriebssystems aus der Laufzeit seit dem Start; wrapfrei (64 Bit).</summary>
        private static DateTime BootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }
}
