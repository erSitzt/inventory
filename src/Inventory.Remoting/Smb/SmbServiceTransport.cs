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

namespace Inventory.Remoting.Smb;

/// <summary>
/// Agentenloser Zugriff wie bei PsExec: Runner und Job über ADMIN$ kopieren, per Service Control Manager
/// als temporären Dienst (LocalSystem) starten, Fortschritt und Ergebnis über die Freigabe lesen, danach aufräumen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SmbServiceTransport(RunnerPackage runner) : IRemoteTransport
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>So lange darf nach dem Ende des Dienstes das Ergebnis noch auf sich warten lassen.</summary>
    private static readonly TimeSpan ResultGrace = TimeSpan.FromSeconds(5);

    public TransportKind Kind => TransportKind.SmbService;

    public Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct) =>
        // Eigener Thread: die Impersonation hängt am Thread, und alle Aufrufe darunter sind blockierend.
        Task.Factory.StartNew(() => RunImpersonated(target, () => RunCore(target.Host, job, files, progress, ct)),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static JobResult RunImpersonated(RemoteTarget target, Func<JobResult> action)
    {
        if (target.Credential is null)
            return action();

        var (user, domain) = SplitUsername(target.Credential, target.Host);
        // NEW_CREDENTIALS: lokal bleibt die Identität des Servers, nur Netzwerkzugriffe (SMB, RPC) nutzen das angegebene Konto.
        // Die Anmeldung wird dabei erst beim ersten Zugriff auf den Zielrechner geprüft.
        if (!LogonUserW(user, domain, target.Credential.Password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out var token))
            throw new RemoteException($"Anmeldung als {target.Credential.UserName} nicht möglich: {Describe(Marshal.GetLastWin32Error())}");
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
        var serviceName = "InventoryJob_" + job.JobId;

        try
        {
            EnsureRoot(root);
            EnsureRunner(Path.Combine(root, JobFiles.RunnerFolder, runner.Hash));
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
            throw new RemoteException($"Kopieren nach \\\\{host}\\ADMIN$ fehlgeschlagen: {DescribeIo(ex)}", ex);
        }

        ServiceHandle? service = null;
        try
        {
            using var scm = OpenSCManagerW(host, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
            if (scm.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new RemoteException(error == 5 && IsLocalMachine(host)
                    ? $"Dienststeuerung auf {host} verweigert den Zugriff. {host} ist dieser Server selbst: lokale Zugriffe laufen immer mit dem Konto des " +
                      "Server-Prozesses, nicht mit den hinterlegten Zugangsdaten. Den Server als Dienst bzw. mit Administratorrechten starten oder einen anderen Rechner als Ziel wählen."
                    : $"Dienststeuerung auf {host} nicht erreichbar: {Describe(error)}");
            }

            // %SystemRoot% wird vom Zielrechner aufgelöst; der Runner findet das Job-Verzeichnis über die ID selbst.
            var binPath = $"\"%SystemRoot%\\{JobFiles.RemoteRoot}\\{JobFiles.RunnerFolder}\\{runner.Hash}\\{JobFiles.RunnerExe}\" {RunnerModes.Service} {job.JobId}";
            service = CreateServiceW(scm, serviceName, "Inventory Job " + job.JobId, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                SERVICE_DEMAND_START, SERVICE_ERROR_IGNORE, binPath, null, IntPtr.Zero, null, null, null);
            if (service.IsInvalid)
                throw new RemoteException($"Dienst auf {host} konnte nicht angelegt werden: {Describe(Marshal.GetLastWin32Error())}");

            if (!StartServiceW(service, 0, IntPtr.Zero))
                throw new RemoteException($"Dienst auf {host} konnte nicht gestartet werden: {Describe(Marshal.GetLastWin32Error())}");

            return WaitForResult(host, jobDir, service, job, progress, ct);
        }
        finally
        {
            if (service is { IsInvalid: false })
            {
                ControlService(service, SERVICE_CONTROL_STOP, out _);
                DeleteService(service);
                service.Dispose();
            }
            TryDeleteDirectory(jobDir);
        }
    }

    internal static bool IsLocalMachine(string host)
    {
        var name = host.Split('.')[0];
        return name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
               || name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1";
    }

    private static JobResult WaitForResult(string host, string jobDir, ServiceHandle service, JobManifest job, IProgress<ProgressEvent>? progress, CancellationToken ct)
    {
        var resultPath = Path.Combine(jobDir, JobFiles.Result);
        var progressPath = Path.Combine(jobDir, JobFiles.Progress);
        long progressOffset = 0;
        DateTime? stoppedSince = null;
        var cancelRequested = false;

        while (true)
        {
            if (ct.IsCancellationRequested && !cancelRequested)
            {
                // Der Runner bricht den laufenden Schritt ab und schreibt trotzdem noch ein Ergebnis.
                ControlService(service, SERVICE_CONTROL_STOP, out _);
                cancelRequested = true;
            }

            progressOffset = PumpProgress(progressPath, progressOffset, progress);

            if (File.Exists(resultPath))
            {
                PumpProgress(progressPath, progressOffset, progress);
                return JobJson.Deserialize<JobResult>(ReadShared(resultPath))
                       ?? throw new RemoteException($"Leeres Ergebnis von {host}");
            }

            if (QueryServiceStatus(service, out var status) && status.dwCurrentState == SERVICE_STOPPED)
            {
                stoppedSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stoppedSince > ResultGrace)
                    throw new RemoteException($"Der Runner auf {host} wurde beendet, ohne ein Ergebnis zu liefern (Exitcode {status.dwWin32ExitCode}). " +
                                              "Mögliche Ursachen: .NET Framework 4.8 fehlt, Virenscanner oder AppLocker blockiert die Ausführung.");
            }

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

    private void EnsureRunner(string runnerDir)
    {
        // Die Markerdatei entsteht zuletzt: ein abgebrochener Kopiervorgang wird beim nächsten Mal wiederholt.
        var marker = Path.Combine(runnerDir, ".complete");
        if (File.Exists(marker))
            return;
        Directory.CreateDirectory(runnerDir);
        foreach (var file in runner.Files)
            File.Copy(file, Path.Combine(runnerDir, Path.GetFileName(file)), overwrite: true);
        File.WriteAllText(marker, runner.Hash);
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
