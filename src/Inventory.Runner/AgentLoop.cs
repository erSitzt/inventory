using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    // Vor der ersten Abfrage: eine Fortsetzung nach dem Neustart braucht den Server nicht. Der Schlüssel liegt
                    // dann schon vor (ohne Anmeldung gibt es keinen Job und damit nichts fortzusetzen).
                    ContinueAfterRestart();
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
                ContinueAfterRestart();
                DeliverPending();
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

            Finish(jobId, jobDir, offer.Manifest, result, 0);
        }

        /// <summary>
        /// Legt das Ergebnis ab und meldet es. Steht nach einem Neustart noch etwas aus, bleibt das Job-Verzeichnis mit einem
        /// Folgeauftrag liegen: <see cref="ContinueAfterRestart"/> setzt dort fort, sobald der Rechner neu gestartet ist.
        /// </summary>
        /// <param name="sequence">0 = Ergebnis des Jobs selbst, 1, 2 … = Ergebnisse der Fortsetzungen nach je einem Neustart.</param>
        private void Finish(string jobId, string jobDir, JobManifest manifest, JobResult result, int sequence)
        {
            Queue(jobId, result, sequence, resumes: result.Success && result.ResumeFromStep != null && !_stop.IsCancellationRequested);
            Advance(jobId, jobDir, manifest, result, sequence);
            DeliverPending();
        }

        /// <summary>
        /// Erst das Ergebnis ablegen, dann den Folgeauftrag: so läuft keine Fortsetzung, deren Vorgänger nicht zur Meldung
        /// bereitliegt. Bricht der Agent dazwischen ab, wartet der Server vergeblich und gibt nach der Frist mit einer Meldung auf.
        /// </summary>
        private void Queue(string jobId, JobResult result, int sequence, bool resumes)
        {
            // Scheitert der Runner, bevor er das Manifest gelesen hat, fehlt dem Ergebnis die Kennung des Jobs.
            if (string.IsNullOrEmpty(result.JobId))
                result.JobId = jobId;
            result.Sequence = sequence;
            result.ResumesLocally = resumes;
            Directory.CreateDirectory(PendingDir);
            WriteAtomically(PendingPath(jobId, sequence), JobJson.Serialize(result));
        }

        /// <summary>Nach dem Ablegen: den nächsten Folgeauftrag hinterlegen oder das Job-Verzeichnis aufräumen.</summary>
        private void Advance(string jobId, string jobDir, JobManifest manifest, JobResult result, int sequence)
        {
            if (result.ResumesLocally && result.ResumeFromStep is int next)
            {
                try
                {
                    var state = new ResumeState { Manifest = manifest.ContinueFrom(next, result.BootTimeUtc), Sequence = sequence + 1, SavedUtc = DateTime.UtcNow };
                    // Ersetzt auch den Folgeauftrag, der gerade gelaufen ist (zweiter Neustart in der Fortsetzung).
                    WriteAtomically(Path.Combine(jobDir, ResumeFile), JobJson.Serialize(state));
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Der Server wartet schon auf die Fortsetzung: ihm gleich als deren Ergebnis melden, dass sie nicht kommt.
                    Log("Fortsetzung nach dem Neustart nicht ablegbar: " + ex.Message);
                    Queue(jobId, JobResult.Failed(jobId, "Agent: the continuation after the restart could not be stored: " + ex.Message), sequence + 1, resumes: false);
                }
            }
            try { Directory.Delete(jobDir, true); } catch (IOException) { /* wird beim nächsten Job nicht stören */ }
        }

        private string PendingPath(string jobId, int sequence) => Path.Combine(PendingDir, sequence == 0 ? jobId + ".json" : $"{jobId}.{sequence}.json");

        /// <summary>Job-ID und Folgenummer aus dem Namen einer abgelegten Ergebnisdatei (ohne Nummer = 0).</summary>
        internal static (string JobId, int Sequence) ParsePendingName(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var dot = name.IndexOf('.');
            if (dot < 0)
                return (name, 0);
            return int.TryParse(name.Substring(dot + 1), out var sequence) ? (name.Substring(0, dot), sequence) : (name.Substring(0, dot), 0);
        }

        /// <summary>Über eine Zwischendatei: ein Abbruch mitten im Schreiben hinterlässt so keine halbe Datei unter dem endgültigen Namen.</summary>
        private static void WriteAtomically(string path, string content)
        {
            File.WriteAllText(path + ".tmp", content, new UTF8Encoding(false));
            File.Delete(path);
            File.Move(path + ".tmp", path);
        }

        private const string ResumeFile = "resume.json";

        /// <summary>Folgeauftrag nach einem Neustart, im Job-Verzeichnis neben den Paketdateien.</summary>
        private sealed class ResumeState
        {
            public JobManifest Manifest { get; set; } = new JobManifest();
            /// <summary>Folgenummer des Ergebnisses dieser Fortsetzung (1 = erste).</summary>
            public int Sequence { get; set; }
            public DateTime SavedUtc { get; set; }
            /// <summary>true = die Fortsetzung hat begonnen; findet sie sich so wieder, wurde sie unterbrochen.</summary>
            public bool Started { get; set; }
        }

        /// <summary>So lange wartet ein Folgeauftrag auf den Neustart, danach meldet er einen Fehler und verfällt.</summary>
        private static readonly TimeSpan ResumeLimit = TimeSpan.FromDays(PendingDays);

        /// <summary>
        /// Setzt Jobs fort, die mit einem Neustart endeten, sobald der Rechner tatsächlich neu gestartet ist – ohne den Server zu
        /// fragen, damit auch ein Notebook ohne Verbindung zum Server weitermacht. Das Ergebnis geht über die Ablage an den Server.
        /// </summary>
        private void ContinueAfterRestart()
        {
            var jobsDir = Path.Combine(_baseDir, JobFiles.JobsFolder);
            if (!Directory.Exists(jobsDir))
                return;
            foreach (var jobDir in Directory.GetDirectories(jobsDir))
            {
                if (_stop.IsCancellationRequested)
                    return;
                var statePath = Path.Combine(jobDir, ResumeFile);
                if (!File.Exists(statePath))
                    continue;
                var jobId = Path.GetFileName(jobDir);
                ResumeState? state;
                try
                {
                    state = JobJson.Deserialize<ResumeState>(File.ReadAllText(statePath, Encoding.UTF8));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is FormatException
                                           || ex is System.Text.Json.JsonException)
                {
                    state = null;
                }
                if (state == null)
                {
                    // Die Folgenummer ist unbekannt; meist ist es die erste Fortsetzung. Passt sie nicht, lehnt der Server ab und gibt nach der Frist auf.
                    Finish(jobId, jobDir, new JobManifest { JobId = jobId }, JobResult.Failed(jobId, "Agent: the continuation after the restart could not be read"), 1);
                    continue;
                }
                if (state.Started)
                {
                    if (File.Exists(PendingPath(jobId, state.Sequence)))
                    {
                        // Die Fortsetzung war fertig und ihr Ergebnis abgelegt; nur der nächste Schritt danach fehlte noch.
                        JobResult? queued;
                        try
                        {
                            queued = JobJson.Deserialize<JobResult>(File.ReadAllText(PendingPath(jobId, state.Sequence), Encoding.UTF8));
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // Vorübergehend nicht lesbar: beim nächsten Durchlauf erneut, die übrigen Jobs laufen weiter.
                            continue;
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            queued = null;
                        }
                        if (queued == null)
                        {
                            // Abgelegtes Ergebnis unbrauchbar: als fehlgeschlagene Fortsetzung melden, nichts wiederholen.
                            Finish(jobId, jobDir, state.Manifest, JobResult.Failed(jobId, "Agent: the result of the continuation after the restart could not be read"), state.Sequence);
                            continue;
                        }
                        Advance(jobId, jobDir, state.Manifest, queued, state.Sequence);
                    }
                    else
                    {
                        // Der Agent oder der Rechner wurde mitten in der Fortsetzung beendet: nicht blind wiederholen. War das
                        // Ergebnis schon zugestellt, lehnt der Server diese Meldung anhand der Folgenummer ab.
                        Finish(jobId, jobDir, state.Manifest, JobResult.Failed(jobId, "Agent: the continuation after the restart was interrupted"), state.Sequence);
                    }
                    continue;
                }
                var previousBoot = state.Manifest.RequireBootAfterUtc;
                if (previousBoot is DateTime boot && JobExecutor.BootTimeUtc() <= boot.AddSeconds(5))
                {
                    if (DateTime.UtcNow - state.SavedUtc > ResumeLimit)
                        Finish(jobId, jobDir, state.Manifest, JobResult.Failed(jobId, "Agent: the computer did not restart"), state.Sequence);
                    continue;
                }

                Log($"Setze {jobId} nach dem Neustart fort.");
                state.Started = true;
                WriteAtomically(statePath, JobJson.Serialize(state));
                WriteAtomically(Path.Combine(jobDir, JobFiles.Manifest), JobJson.Serialize(state.Manifest));
                File.Delete(Path.Combine(jobDir, JobFiles.Progress));
                JobResult result;
                try
                {
                    var executor = new JobExecutor(jobDir);
                    _running = executor;
                    result = executor.Run();
                }
                catch (Exception ex)
                {
                    result = JobResult.Failed(jobId, "Agent: " + ex.Message);
                }
                finally
                {
                    _running = null;
                }
                // Der Folgeauftrag bleibt (als begonnen markiert) liegen, bis das Ergebnis abgelegt ist; Advance ersetzt oder entfernt ihn.
                Finish(jobId, jobDir, state.Manifest, result, state.Sequence);
            }
        }

        private string PendingDir => Path.Combine(_baseDir, "pending");

        /// <summary>
        /// Meldet abgelegte Ergebnisse. Ein Ergebnis bleibt liegen, bis der Server es angenommen oder als unbekannt abgelehnt
        /// hat – also auch über einen Neustart des Servers oder des Rechners hinweg –, höchstens aber <see cref="PendingDays"/> Tage.
        /// Je Job in der Reihenfolge der Folgenummern, und ein späteres erst, wenn das vorige angenommen oder endgültig abgelehnt ist.
        /// </summary>
        private void DeliverPending()
        {
            if (!Directory.Exists(PendingDir))
                return;
            var files = Directory.GetFiles(PendingDir, "*.json")
                .Select(path => new { Path = path, Name = ParsePendingName(path) })
                .OrderBy(f => f.Name.JobId, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name.Sequence)
                .ToList();
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var jobId = file.Name.JobId;
                if (blocked.Contains(jobId))
                    continue;
                try
                {
                    // Verfällt ein Ergebnis, verfallen die späteren desselben Jobs mit: ohne Vorgänger nähme der Server sie nicht an.
                    if (expired.Contains(jobId) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file.Path) > TimeSpan.FromDays(PendingDays))
                    {
                        expired.Add(jobId);
                        File.Delete(file.Path);
                        continue;
                    }
                    using (var content = new StringContent(File.ReadAllText(file.Path, Encoding.UTF8), Encoding.UTF8, "application/json"))
                    using (var response = _http.PostAsync($"{AgentProtocol.JobsPath}/{jobId}/result", content, _stop.Token).GetAwaiter().GetResult())
                    {
                        var status = (int)response.StatusCode;
                        // Angenommen, oder der Server will es nicht (Job unbekannt, Ergebnis unlesbar, Rechner gelöscht): beides ist endgültig.
                        if (response.IsSuccessStatusCode || (status >= 400 && status < 500 && status != 408 && status != 429))
                            File.Delete(file.Path);
                        else
                            blocked.Add(jobId);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException)
                {
                    // Server nicht erreichbar: beim nächsten Durchlauf erneut.
                    return;
                }
            }
        }

        private const int PendingDays = 7;

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
