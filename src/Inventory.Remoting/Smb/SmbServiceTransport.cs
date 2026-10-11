using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.Win32.SafeHandles;
using static Inventory.Remoting.Smb.NativeMethods;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Remoting.Smb;

/// <summary>
/// Agentenloser Zugriff über ADMIN$ und den Service Control Manager: Runner und Job kopieren, den Runner-Dienst
/// (LocalSystem) mit der Job-ID starten, Fortschritt und Ergebnis über die Freigabe lesen, danach den Job aufräumen.
/// Runner und Dienst bleiben auf dem Zielrechner bestehen: ein fester Dienstname und ein fester Ablageort lassen sich
/// im Endpunktschutz gezielt freigeben, und es entsteht nicht bei jedem Job ein neuer Dienst.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SmbServiceTransport(RunnerPackage runner, IStringLocalizer<SharedResource>? localizer = null) : IRemoteTransport
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>So lange darf nach dem Ende des Dienstes das Ergebnis noch auf sich warten lassen.</summary>
    private static readonly TimeSpan ResultGrace = TimeSpan.FromSeconds(5);
    /// <summary>Der Dienst führt einen Job nach dem anderen aus; so lange wartet ein Job höchstens auf den vorigen.</summary>
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OldRunnerAge = TimeSpan.FromDays(7);

    public TransportKind Kind => TransportKind.SmbService;

    public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct) =>
        // Eigener Thread: die Impersonation hängt am Thread, und alle Aufrufe darunter sind blockierend.
        Task.Factory.StartNew(() => RunImpersonated(target, () => RunCore(target.Host, job, files, progress, ct)),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private JobResult RunImpersonated(RemoteTarget target, Func<JobResult> action)
    {
        if (target.Credential is null)
            return action();

        var (user, domain) = SplitUsername(target.Credential, target.Host);
        // NEW_CREDENTIALS: lokal bleibt die Identität des Servers, nur Netzwerkzugriffe (SMB, RPC) nutzen das angegebene Konto.
        // Die Anmeldung wird dabei erst beim ersten Zugriff auf den Zielrechner geprüft.
        if (!LogonUserW(user, domain, target.Credential.Password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out var token))
            throw new RemoteException(L["Logon as {0} not possible: {1}", target.Credential.UserName, L[Describe(Marshal.GetLastWin32Error())]]);
        using (token)
            return WindowsIdentity.RunImpersonated(token, action);
    }

    internal static (string User, string? Domain) SplitUsername(NetworkCredential credential, string host)
    {
        var name = credential.UserName;
        var slash = name.IndexOf('\\');
        if (slash >= 0)
        {
            var domain = name[..slash];
            return (name[(slash + 1)..], domain == "." ? host : domain);
        }
        // user@domäne (UPN) wird ohne Domänenangabe übergeben.
        return name.Contains('@') ? (name, null) : (name, string.IsNullOrEmpty(credential.Domain) ? host : credential.Domain);
    }

    private JobResult RunCore(string host, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
    {
        var root = $@"\\{host}\ADMIN$\{JobFiles.RemoteRoot}";
        var jobDir = Path.Combine(root, JobFiles.JobsFolder, job.JobId);

        try
        {
            EnsureRoot(root);
            EnsureRunner(Path.Combine(root, JobFiles.RunnerFolder), runner.Hash);
            Directory.CreateDirectory(jobDir);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.Combine(jobDir, JobFiles.FilesFolder, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file.LocalPath, destination, overwrite: true);
            }
            File.WriteAllText(Path.Combine(jobDir, JobFiles.Manifest), JobJson.Serialize(job, indented: true), new UTF8Encoding(false));
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(jobDir);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(jobDir);
            throw new RemoteException(L["Copying to \\\\{0}\\ADMIN$ failed: {1}", host, DescribeIo(ex)], ex);
        }

        ServiceHandle? service = null;
        // Ab hier gehört der laufende Dienst nicht mehr sicher diesem Job: er hat sein Ergebnis geliefert oder sich beendet,
        // und der Dienst kann bereits den nächsten Job ausführen.
        var released = true;
        try
        {
            using var scm = OpenSCManagerW(host, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
            if (scm.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new RemoteException(error == 5 && IsLocalMachine(host)
                    ? L["Service control on {0} denies access. {0} is this server itself: local access always runs with the account of the server process, not with the stored credentials. Run the server as a service or with administrator rights, or choose another computer as target.", host]
                    : L["Service control on {0} unreachable: {1}", host, L[Describe(error)]]);
            }

            service = OpenRunnerService(scm, host);
            StartRunner(service, host, job.JobId, ct);
            released = false;
            return WaitForResult(host, jobDir, service, job, progress, ct, ref released);
        }
        finally
        {
            // Nur den eigenen, noch laufenden Job anhalten (etwa nach einem Lesefehler), nie den eines anderen.
            if (service is { IsInvalid: false } && !released)
                ControlService(service, SERVICE_CONTROL_STOP, out _);
            service?.Dispose();
            TryDeleteDirectory(jobDir);
        }
    }

    /// <summary>Öffnet den Runner-Dienst; legt ihn beim ersten Mal an und stellt ihn nach einem Runner-Update auf die neue Ablage um.</summary>
    private ServiceHandle OpenRunnerService(ServiceHandle scm, string host)
    {
        // %SystemRoot% wird vom Zielrechner aufgelöst; der Runner findet das Job-Verzeichnis über die ID selbst.
        var binPath = $"\"%SystemRoot%\\{JobFiles.RemoteRoot}\\{JobFiles.RunnerFolder}\\{runner.Hash}\\{JobFiles.RunnerExe}\" {RunnerModes.Service}";
        var service = OpenServiceW(scm, JobFiles.RunnerServiceName, SERVICE_ALL_ACCESS);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            if (error != ERROR_SERVICE_DOES_NOT_EXIST)
                throw new RemoteException(L["Service on {0} could not be opened: {1}", host, L[Describe(error)]]);

            service = CreateServiceW(scm, JobFiles.RunnerServiceName, "Inventory Runner", SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                SERVICE_DEMAND_START, SERVICE_ERROR_IGNORE, binPath, null, IntPtr.Zero, null, null, null);
            if (service.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                service.Dispose();
                // Ein gleichzeitig gestarteter Job war schneller.
                if (error == ERROR_SERVICE_EXISTS)
                    service = OpenServiceW(scm, JobFiles.RunnerServiceName, SERVICE_ALL_ACCESS);
                if (service.IsInvalid)
                    throw new RemoteException(L["Service on {0} could not be created: {1}", host, L[Describe(error)]]);
            }
            return service;
        }

        if (!string.Equals(BinaryPath(service), binPath, StringComparison.OrdinalIgnoreCase)
            && !ChangeServiceConfigW(service, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, binPath, null, IntPtr.Zero, null, null, null, null))
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            throw new RemoteException(L["Service on {0} could not be updated: {1}", host, L[Describe(error)]]);
        }
        return service;
    }

    /// <summary>Startet den Dienst mit der Job-ID; läuft dort noch ein anderer Job, wird gewartet.</summary>
    private void StartRunner(ServiceHandle service, string host, string jobId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BusyTimeout;
        while (!StartServiceW(service, 1, [jobId]))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not (ERROR_SERVICE_ALREADY_RUNNING or ERROR_SERVICE_CANNOT_ACCEPT_CTRL))
                throw new RemoteException(L["Service on {0} could not be started: {1}", host, L[Describe(error)]]);
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new RemoteException(L["Another job has been running on {0} for more than {1} minutes; this job was not started.", host, (int)BusyTimeout.TotalMinutes]);
            Thread.Sleep(BusyPollInterval);
        }
    }

    internal bool IsLocalMachine(string host)
    {
        var name = host.Split('.')[0];
        return name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
               || name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1";
    }

    private JobResult WaitForResult(string host, string jobDir, ServiceHandle service, JobManifest job, IProgress<ProgressEvent>? progress, CancellationToken ct, ref bool released)
    {
        var resultPath = Path.Combine(jobDir, JobFiles.Result);
        var progressPath = Path.Combine(jobDir, JobFiles.Progress);
        long progressOffset = 0;
        DateTime? stoppedSince = null;
        var cancelRequested = false;
        uint exitCode = 0;

        while (true)
        {
            if (ct.IsCancellationRequested && !cancelRequested && !released)
            {
                // Der Runner bricht den laufenden Schritt ab und schreibt trotzdem noch ein Ergebnis.
                ControlService(service, SERVICE_CONTROL_STOP, out _);
                cancelRequested = true;
            }

            progressOffset = PumpProgress(progressPath, progressOffset, progress);

            if (File.Exists(resultPath))
            {
                released = true;
                PumpProgress(progressPath, progressOffset, progress);
                return JobJson.Deserialize<JobResult>(ReadShared(resultPath))
                       ?? throw new RemoteException(L["Empty result from {0}", host]);
            }

            if (QueryServiceStatus(service, out var status) && status.dwCurrentState == SERVICE_STOPPED)
            {
                released = true;
                stoppedSince ??= DateTime.UtcNow;
                exitCode = status.dwWin32ExitCode;
            }
            // Auch wenn der Dienst inzwischen für einen anderen Job wieder läuft: dieser Job hat kein Ergebnis hinterlassen.
            if (stoppedSince is not null && DateTime.UtcNow - stoppedSince > ResultGrace)
                throw new RemoteException(L["The runner on {0} exited without delivering a result (exit code {1}). Possible causes: .NET Framework 4.8 is missing, or antivirus or AppLocker blocks execution.", host, exitCode]);

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>Liest neu hinzugekommene vollständige Zeilen der Fortschrittsdatei und liefert die neue Leseposition.</summary>
    internal static long PumpProgress(string path, long offset, IProgress<ProgressEvent>? progress)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= offset)
                return offset;
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - offset];
            stream.ReadExactly(buffer);

            // Nur bis zum letzten Zeilenende verarbeiten; eine angefangene Zeile kommt beim nächsten Durchlauf.
            var end = Array.LastIndexOf(buffer, (byte)'\n') + 1;
            foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (JobJson.Deserialize<ProgressEvent>(line) is { } e)
                    progress?.Report(e);
            }
            return offset + end;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Datei existiert noch nicht oder ist kurz gesperrt.
            return offset;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Legt %SystemRoot%\Inventory an und beschränkt den Zugriff auf SYSTEM und Administratoren, da Jobs Geheimnisse enthalten können.</summary>
    private static void EnsureRoot(string root)
    {
        if (Directory.Exists(root))
            return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(root).Create(security);
    }

    private void EnsureRunner(string runnersDir, string hash)
    {
        var runnerDir = Path.Combine(runnersDir, hash);
        // Die Markerdatei entsteht zuletzt: ein abgebrochener Kopiervorgang wird beim nächsten Mal wiederholt.
        var marker = Path.Combine(runnerDir, ".complete");
        if (File.Exists(marker))
            return;
        Directory.CreateDirectory(runnerDir);
        foreach (var file in runner.Files)
            File.Copy(file, Path.Combine(runnerDir, Path.GetFileName(file)), overwrite: true);
        File.WriteAllText(marker, runner.Hash);
        RemoveOldRunners(runnersDir, hash);
    }

    /// <summary>Räumt Runner früherer Versionen weg, damit auf dem Zielrechner nur eine ausführbare Fassung liegt.</summary>
    private static void RemoveOldRunners(string runnersDir, string keep)
    {
        try
        {
            foreach (var old in Directory.GetDirectories(runnersDir))
            {
                if (string.Equals(Path.GetFileName(old), keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Frisch abgelegte Runner gehören womöglich einem zweiten Server anderer Version, der gerade einen Job startet.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(old, ".complete")) < OldRunnerAge)
                    continue;
                try
                {
                    Directory.Delete(old, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Dort läuft noch ein Job; der nächste Versionswechsel räumt nach.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        // Der Runner gibt seine Dateien erst kurz nach dem Dienstende frei.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static string DescribeIo(Exception ex) =>
        // HResult von IOExceptions trägt den Win32-Fehlercode in den unteren 16 Bit.
        ex is UnauthorizedAccessException ? Describe(5) : Describe(ex.HResult & 0xFFFF);
}
