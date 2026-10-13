using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using Inventory.Contracts;

namespace Inventory.Runner
{
    /// <summary>
    /// Dauerbetrieb als Agent: meldet sich einmalig beim Server an, fragt dann fortlaufend nach Jobs,
    /// führt sie mit demselben <see cref="JobExecutor"/> aus wie im agentenlosen Betrieb und liefert Fortschritt und Ergebnis.
    /// </summary>
    internal sealed class AgentLoop
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

        private readonly string _baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly string _version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        private HttpClient _http = null!;
        private JobExecutor? _running;

        public void Stop()
        {
            _stop.Cancel();
            _running?.Cancel();
        }

        public void Run()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            RemoveReplacedFiles();
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var config = JobJson.Deserialize<AgentConfig>(File.ReadAllText(Path.Combine(_baseDir, AgentProtocol.ConfigFile), Encoding.UTF8))
                                 ?? throw new InvalidDataException("agent.json ist leer");
                    // Der Server hält die Anfrage offen, bis ein Job da ist; das Zeitlimit muss darüber liegen.
                    _http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
                    _http.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, GetOrCreateKey(config));
                    PollUntilStopped();
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    Log("Fehler: " + ex.Message);
                    _stop.Token.WaitHandle.WaitOne(RetryDelay);
                }
            }
        }

        private string GetOrCreateKey(AgentConfig config)
        {
            var keyPath = Path.Combine(_baseDir, AgentProtocol.KeyFile);
            if (File.Exists(keyPath))
                return File.ReadAllText(keyPath).Trim();

            var request = new AgentEnrollRequest { EnrollmentToken = config.EnrollmentToken, HostName = Environment.MachineName, DnsName = DnsName(), Version = _version };
            using (var response = _http.PostAsync(AgentProtocol.EnrollPath, Json(request)).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Anmeldung am Server abgelehnt ({(int)response.StatusCode})");
                var key = JobJson.Deserialize<AgentEnrollResponse>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AgentKey;
                File.WriteAllText(keyPath, key);
                Log("Am Server angemeldet.");
                return key;
            }
        }

        /// <summary>Rechnername mit primärem DNS-Suffix, ohne Namensauflösung; null, wenn der Rechner kein Suffix hat.</summary>
        private static string? DnsName()
        {
            try
            {
                var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                return string.IsNullOrWhiteSpace(properties.DomainName) ? null : properties.HostName + "." + properties.DomainName;
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
                return null;
            }
        }

        private void PollUntilStopped()
        {
            while (!_stop.IsCancellationRequested)
            {
                using (var response = _http.PostAsync(AgentProtocol.PollPath, Json(new AgentPollRequest { Version = _version, RunnerHash = InstalledHash() }), _stop.Token).GetAwaiter().GetResult())
                {
                    if (response.StatusCode == HttpStatusCode.NoContent)
                        continue;
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        // Der Server kennt den Schlüssel nicht mehr (Rechner dort gelöscht): neu anmelden.
                        File.Delete(Path.Combine(_baseDir, AgentProtocol.KeyFile));
                        throw new InvalidOperationException("Schlüssel vom Server abgelehnt, melde neu an");
                    }
                    response.EnsureSuccessStatusCode();
                    var offer = JobJson.Deserialize<AgentJobOffer>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                    if (offer.Update != null)
                        ApplyUpdate(offer.Update);
                    else
                        RunJob(offer);
                }
            }
        }

        private string InstalledHash()
        {
            var path = Path.Combine(_baseDir, AgentProtocol.VersionFile);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }

        /// <summary>
        /// Tauscht die eigenen Programmdateien aus. Laufende Dateien lassen sich unter Windows nicht überschreiben,
        /// aber umbenennen: die alte wird zu *.old, die neue nimmt ihren Platz ein. Danach beendet sich der Prozess
        /// mit Fehlercode, woraufhin die Wiederherstellung des Dienstes ihn mit den neuen Dateien neu startet.
        /// </summary>
        private void ApplyUpdate(AgentUpdateOffer update)
        {
            Log($"Aktualisiere auf {update.Hash} ...");
            var staging = Path.Combine(_baseDir, "update");
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            // Erst alles herunterladen: bricht die Verbindung ab, bleibt die laufende Installation unberührt.
            for (var i = 0; i < update.Files.Count; i++)
            {
                var name = Path.GetFileName(update.Files[i]);
                using (var response = _http.GetAsync($"{AgentProtocol.UpdatePath}/{i}", HttpCompletionOption.ResponseHeadersRead, _stop.Token).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    using (var file = File.Create(Path.Combine(staging, name)))
                        response.Content.CopyToAsync(file).GetAwaiter().GetResult();
                }
            }

            foreach (var source in Directory.GetFiles(staging))
            {
                var target = Path.Combine(_baseDir, Path.GetFileName(source));
                if (File.Exists(target))
                {
                    var old = target + OldSuffix;
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(target, old);
                }
                File.Move(source, target);
            }
            Directory.Delete(staging, true);
            File.WriteAllText(Path.Combine(_baseDir, AgentProtocol.VersionFile), update.Hash);
            Log("Aktualisiert, starte neu.");
            Environment.Exit(RestartExitCode);
        }

        private const string OldSuffix = ".old";
        /// <summary>Ungleich 0, damit der Dienststeuerungs-Manager das Ende als Fehler wertet und den Dienst neu startet.</summary>
        private const int RestartExitCode = 3;

        /// <summary>Räumt die beim letzten Update umbenannten Dateien weg; jetzt hält sie kein Prozess mehr offen.</summary>
        private void RemoveReplacedFiles()
        {
            foreach (var old in Directory.GetFiles(_baseDir, "*" + OldSuffix))
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private void RunJob(AgentJobOffer offer)
        {
            var jobId = offer.Manifest.JobId;
            var jobDir = Path.Combine(_baseDir, JobFiles.JobsFolder, jobId);
            JobResult result;
            try
            {
                Directory.CreateDirectory(jobDir);
                for (var i = 0; i < offer.Files.Count; i++)
                {
                    var target = Path.Combine(jobDir, JobFiles.FilesFolder, offer.Files[i]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var response = _http.GetAsync($"{AgentProtocol.JobsPath}/{jobId}/files/{i}", HttpCompletionOption.ResponseHeadersRead, _stop.Token).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        using (var file = File.Create(target))
                            response.Content.CopyToAsync(file).GetAwaiter().GetResult();
                    }
                }
                File.WriteAllText(Path.Combine(jobDir, JobFiles.Manifest), JobJson.Serialize(offer.Manifest), new UTF8Encoding(false));

                var executor = new JobExecutor(jobDir);
                _running = executor;
                using (var pump = new Timer(_ => SendProgress(jobId, jobDir), null, 1000, 1000))
                {
                    result = executor.Run();
                    pump.Change(Timeout.Infinite, Timeout.Infinite);
                }
                SendProgress(jobId, jobDir);
            }
            catch (Exception ex)
            {
                result = JobResult.Failed(jobId, "Agent: " + ex.Message);
            }
            finally
            {
                _running = null;
            }

            _http.PostAsync($"{AgentProtocol.JobsPath}/{jobId}/result", Json(result)).GetAwaiter().GetResult().Dispose();
            try { Directory.Delete(jobDir, true); } catch (IOException) { /* wird beim nächsten Job nicht stören */ }
        }

        private long _progressOffset;
        private string? _progressJob;
        private readonly object _progressLock = new object();

        /// <summary>Schickt neu hinzugekommene vollständige Zeilen der Fortschrittsdatei an den Server.</summary>
        private void SendProgress(string jobId, string jobDir)
        {
            if (!Monitor.TryEnter(_progressLock))
                return;
            try
            {
                if (_progressJob != jobId)
                {
                    _progressJob = jobId;
                    _progressOffset = 0;
                }
                var path = Path.Combine(jobDir, JobFiles.Progress);
                if (!File.Exists(path))
                    return;
                byte[] buffer;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= _progressOffset)
                        return;
                    stream.Seek(_progressOffset, SeekOrigin.Begin);
                    buffer = new byte[stream.Length - _progressOffset];
                    stream.Read(buffer, 0, buffer.Length);
                }
                var end = Array.LastIndexOf(buffer, (byte)'\n') + 1;
                if (end == 0)
                    return;
                var events = new List<ProgressEvent>();
                foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = JobJson.Deserialize<ProgressEvent>(line);
                    if (e != null)
                        events.Add(e);
                }
                _http.PostAsync($"{AgentProtocol.JobsPath}/{jobId}/progress", Json(events)).GetAwaiter().GetResult().Dispose();
                _progressOffset += end;
            }
            catch (Exception)
            {
                // Fortschritt ist nur Komfort; das Ergebnis wird am Ende unabhängig davon gemeldet.
            }
            finally
            {
                Monitor.Exit(_progressLock);
            }
        }

        private static StringContent Json<T>(T value) => new StringContent(JobJson.Serialize(value), Encoding.UTF8, "application/json");

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(_baseDir, "agent.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
