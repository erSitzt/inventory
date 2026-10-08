using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Inventory.Runner
{
    /// <summary>
    /// Startet Prozesse in der Sitzung des angemeldeten Benutzers. Der Runner läuft als Lokales System in Sitzung 0;
    /// für Dinge im Benutzerkontext (HKCU, Profil, Netzlaufwerke des Benutzers) leiht er sich dessen Anmelde-Token.
    /// </summary>
    internal sealed class UserSession : IDisposable
    {
        private IntPtr _token;

        private UserSession(IntPtr token, string userName)
        {
            _token = token;
            UserName = userName;
        }

        public string UserName { get; }

        /// <summary>Token des angemeldeten Benutzers; null, wenn niemand angemeldet ist. Konsole vor Remotedesktop.</summary>
        /// <exception cref="InvalidOperationException">Der Runner darf das Token nicht abrufen (läuft nicht als Lokales System).</exception>
        public static UserSession? Open()
        {
            var sessionId = FindActiveSession();
            if (sessionId == null)
                return null;

            if (WTSQueryUserToken(sessionId.Value, out var token))
            {
                using (var identity = new WindowsIdentity(token))
                    return new UserSession(token, identity.Name);
            }

            var error = Marshal.GetLastWin32Error();
            // 1314 = erforderliches Recht fehlt: nur Lokales System darf fremde Sitzungs-Token abrufen.
            if (error != 1314 && error != 5)
                throw new Win32Exception(error);

            // Läuft der Runner bereits als genau dieser Benutzer in dessen Sitzung, genügt das eigene Token.
            using (var current = WindowsIdentity.GetCurrent())
            {
                if (System.Diagnostics.Process.GetCurrentProcess().SessionId == sessionId.Value
                    && DuplicateTokenEx(current.Token, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2, 1 /* TokenPrimary */, out var own))
                    return new UserSession(own, current.Name);
            }
            throw new InvalidOperationException(
                "Im Kontext des angemeldeten Benutzers kann nur ausgeführt werden, wenn der Runner als Lokales System läuft (Transport SMB + Dienst oder Agent).");
        }

        /// <summary>Führt das Programm mit dem Benutzer-Token aus und liefert den Exitcode; null bei Zeitüberschreitung.</summary>
        public int? Run(string fileName, string arguments, string workingDirectory, Encoding outputEncoding, Action<string> onLine,
            int timeoutMs, Action<int> onStarted)
        {
            var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
            if (!CreatePipe(out var readHandle, out var writeHandle, ref security, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // Das Lese-Ende bleibt beim Runner; nur das Schreib-Ende darf an den Kindprozess vererbt werden.
            SetHandleInformation(readHandle, 1 /* HANDLE_FLAG_INHERIT */, 0);

            var environment = IntPtr.Zero;
            var process = new PROCESS_INFORMATION();
            try
            {
                // Umgebung des Benutzers (USERPROFILE, APPDATA, TEMP ...), nicht die des Dienstes.
                if (!CreateEnvironmentBlock(out environment, _token, false))
                    environment = IntPtr.Zero;

                var startup = new STARTUPINFO
                {
                    cb = Marshal.SizeOf(typeof(STARTUPINFO)),
                    lpDesktop = @"winsta0\default",
                    dwFlags = 0x00000100 /* STARTF_USESTDHANDLES */,
                    hStdOutput = writeHandle,
                    hStdError = writeHandle,
                    hStdInput = IntPtr.Zero,
                };
                var commandLine = new StringBuilder("\"" + fileName + "\" " + arguments);
                const uint flags = 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */ | 0x08000000 /* CREATE_NO_WINDOW */;
                if (!CreateProcessAsUser(_token, null, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, environment, workingDirectory, ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                // Ohne das Schließen des eigenen Schreib-Endes käme beim Lesen nie ein Dateiende an.
                CloseHandle(writeHandle);
                if (environment != IntPtr.Zero)
                    DestroyEnvironmentBlock(environment);
            }

            try
            {
                onStarted(process.dwProcessId);
                var reader = new Thread(() =>
                {
                    using (var stream = new FileStream(new SafeFileHandle(readHandle, true), FileAccess.Read))
                    using (var text = new StreamReader(stream, outputEncoding))
                    {
                        string? line;
                        while ((line = text.ReadLine()) != null)
                            onLine(line);
                    }
                })
                { IsBackground = true };
                reader.Start();

                if (WaitForSingleObject(process.hProcess, timeoutMs < 0 ? 0xFFFFFFFF : (uint)timeoutMs) != 0)
                    return null;
                reader.Join(10_000);
                GetExitCodeProcess(process.hProcess, out var exitCode);
                return (int)exitCode;
            }
            finally
            {
                CloseHandle(process.hThread);
                CloseHandle(process.hProcess);
            }
        }

        public void Dispose()
        {
            if (_token != IntPtr.Zero)
            {
                CloseHandle(_token);
                _token = IntPtr.Zero;
            }
        }

        private static int? FindActiveSession()
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
                return null;
            try
            {
                var size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                var console = (int)WTSGetActiveConsoleSessionId();
                int? found = null;
                for (var i = 0; i < count; i++)
                {
                    var info = (WTS_SESSION_INFO)Marshal.PtrToStructure(buffer + i * size, typeof(WTS_SESSION_INFO));
                    // Sitzung 0 gehört den Diensten; "aktiv" heißt: jemand ist angemeldet und die Sitzung ist verbunden.
                    if (info.State != 0 /* WTSActive */ || info.SessionId == 0)
                        continue;
                    if (info.SessionId == console)
                        return info.SessionId;
                    found = found ?? info.SessionId;
                }
                return found;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr WinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr duplicate);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SECURITY_ATTRIBUTES attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
