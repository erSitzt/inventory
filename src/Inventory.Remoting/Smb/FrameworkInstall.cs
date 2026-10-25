using System.Runtime.InteropServices;
using System.Text;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using static Inventory.Remoting.Smb.NativeMethods;

namespace Inventory.Remoting.Smb;

public sealed partial class SmbServiceTransport
{
    /// <summary>Eigener, nur kurz bestehender Dienst: er startet den Installer und ist danach wieder weg.</summary>
    internal const string FrameworkServiceName = "InventoryNetFx";
    private const string FrameworkFolder = "NetFx";
    private const string FrameworkInstallerFile = "ndp48.exe";
    private const string FrameworkScript = "install.cmd";
    private const string FrameworkExitCode = "exitcode.txt";
    /// <summary>Der Installer braucht je nach Rechner 5 bis 30 Minuten.</summary>
    private static readonly TimeSpan FrameworkTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan FrameworkPollInterval = TimeSpan.FromSeconds(5);

    public Task<FrameworkInstallResult> InstallAsync(RemoteTarget target, string installer, CancellationToken ct) =>
        Task.Factory.StartNew(() => RunImpersonated(target, () => InstallFrameworkCore(target.Host, installer, ct)),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Kopiert den Offline-Installer über ADMIN$ und startet ihn über einen kurzlebigen Dienst als Lokales System. Der Dienst
    /// startet nur cmd.exe, das den Installer abgekoppelt anstößt und sofort endet; der Dienststeuer-Manager meldet deshalb
    /// einen Startfehler, der Installer läuft aber weiter. Sein Exitcode landet in einer Datei, die der Server abwartet.
    /// </summary>
    private FrameworkInstallResult InstallFrameworkCore(string host, string installer, CancellationToken ct)
    {
        var share = $@"\\{host}\ADMIN$";
        var before = NetFramework.Inspect(share)
                     ?? throw new RemoteException(L["The .NET Framework on {0} could not be checked over ADMIN$.", host]);
        if (before.Sufficient)
            return new FrameworkInstallResult(true, false, L[".NET Framework {0} is already installed.", before.Installed ?? "4.8"]);

        var root = Path.Combine(share, JobFiles.RemoteRoot);
        var folder = Path.Combine(root, FrameworkFolder);
        var exitCode = Path.Combine(folder, FrameworkExitCode);
        try
        {
            EnsureRoot(root);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, FrameworkInstallerFile);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(installer).Length)
                File.Copy(installer, target, overwrite: true);
            File.Delete(exitCode);
            // Die Datei mit dem Exitcode entsteht erst am Ende vollständig (move), damit der Server nie eine halbe liest.
            File.WriteAllText(Path.Combine(folder, FrameworkScript), string.Join("\r\n",
                "@echo off",
                $"\"%~dp0{FrameworkInstallerFile}\" /q /norestart /log \"%~dp0ndp48.log\"",
                "echo %errorlevel%> \"%~dp0exitcode.tmp\"",
                $"move /y \"%~dp0exitcode.tmp\" \"%~dp0{FrameworkExitCode}\" >nul",
                ""), Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RemoteException(L["Copying to \\\\{0}\\ADMIN$ failed: {1}", host, DescribeIo(ex)], ex);
        }

        StartDetached(host, $@"%SystemRoot%\System32\cmd.exe /c start """" /b %SystemRoot%\System32\cmd.exe /c %SystemRoot%\{JobFiles.RemoteRoot}\{FrameworkFolder}\{FrameworkScript}");

        var deadline = DateTime.UtcNow + FrameworkTimeout;
        while (!File.Exists(exitCode))
        {
            if (DateTime.UtcNow > deadline)
                throw new RemoteException(L["The .NET Framework installer on {0} did not finish within {1} minutes.", host, (int)FrameworkTimeout.TotalMinutes], resultMayFollow: true);
            // Ein Abbruch beendet nur das Warten; den laufenden Installer abzubrechen, hinterließe ein halb installiertes Framework.
            ct.WaitHandle.WaitOne(FrameworkPollInterval);
            ct.ThrowIfCancellationRequested();
        }

        int.TryParse(File.ReadAllText(exitCode).Trim(), out var code);
        var after = NetFramework.Inspect(share);
        var result = Interpret(code, after?.Installed);
        // Das Protokoll bleibt bei einem Fehler liegen: es erklärt, woran es scheiterte.
        TryDelete(Path.Combine(folder, FrameworkInstallerFile));
        TryDelete(Path.Combine(folder, FrameworkScript));
        TryDelete(exitCode);
        return result.Success
            ? result
            : result with { Message = result.Message + " " + L["Log: {0}", $@"%SystemRoot%\{JobFiles.RemoteRoot}\{FrameworkFolder}\ndp48.log"] };
    }

    /// <summary>Die dokumentierten Exitcodes des .NET-Framework-Installers.</summary>
    internal FrameworkInstallResult Interpret(int code, string? installed) => code switch
    {
        0 => new(true, false, L[".NET Framework {0} installed.", installed ?? "4.8"]),
        3010 or 1641 => new(true, true, L[".NET Framework 4.8 installed; it takes effect after a restart of the computer."]),
        1602 => new(false, false, L["The .NET Framework installation was cancelled (exit code {0}).", code]),
        5100 => new(false, false, L["The computer does not meet the requirements of .NET Framework 4.8 (exit code 5100), e.g. Windows 7 or Server 2008 R2 without Service Pack 1."]),
        _ => new(false, false, L["The .NET Framework installation failed with exit code {0}.", code]),
    };

    /// <summary>Legt den kurzlebigen Dienst an, startet ihn und entfernt ihn gleich wieder; der angestoßene Prozess läuft weiter.</summary>
    private void StartDetached(string host, string commandLine)
    {
        using var scm = OpenSCManagerW(host, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm.IsInvalid)
            throw new RemoteException(L["Service control on {0} unreachable: {1}", host, L[Describe(Marshal.GetLastWin32Error())]]);
        var service = OpenServiceW(scm, FrameworkServiceName, SERVICE_ALL_ACCESS);
        if (service.IsInvalid)
        {
            service.Dispose();
            service = CreateServiceW(scm, FrameworkServiceName, "Inventory .NET Framework setup", SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                SERVICE_DEMAND_START, SERVICE_ERROR_IGNORE, commandLine, null, IntPtr.Zero, null, null, null);
        }
        else if (!ChangeServiceConfigW(service, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, commandLine, null, IntPtr.Zero, null, null, null, null))
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            throw new RemoteException(L["Service on {0} could not be updated: {1}", host, L[Describe(error)]]);
        }
        using (service)
        {
            if (service.IsInvalid)
                throw new RemoteException(L["Service on {0} could not be created: {1}", host, L[Describe(Marshal.GetLastWin32Error())]]);
            try
            {
                // cmd.exe meldet sich nie als Dienst: ein Fehler wie 1053 (keine Antwort) ist hier der Normalfall.
                if (!StartServiceW(service, 0, null) && Marshal.GetLastWin32Error() is var error && error is 5 or 2 or 3 or 193 or 1058)
                    throw new RemoteException(L["Service on {0} could not be started: {1}", host, L[Describe(error)]]);
            }
            finally
            {
                DeleteService(service);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
