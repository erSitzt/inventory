using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Inventory.Remoting.Smb;

internal static class NativeMethods
{
    public const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    public const int LOGON32_PROVIDER_WINNT50 = 3;

    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;
    public const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    public const uint SERVICE_DEMAND_START = 3;
    public const uint SERVICE_ERROR_IGNORE = 0;
    public const uint SERVICE_CONTROL_STOP = 1;
    public const uint SERVICE_STOPPED = 1;

    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;
    public const int ERROR_SERVICE_EXISTS = 1073;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LogonUserW(string username, string? domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ServiceHandle CreateServiceW(ServiceHandle scm, string serviceName, string displayName, uint desiredAccess,
        uint serviceType, uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ServiceHandle OpenServiceW(ServiceHandle scm, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool StartServiceW(ServiceHandle service, int argc,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? argv);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr config, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ChangeServiceConfigW(ServiceHandle service, uint serviceType, uint startType, uint errorControl, string? binaryPathName,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    /// <summary>Befehlszeile eines Dienstes, wie sie eingetragen ist (Umgebungsvariablen nicht aufgelöst); null, wenn die Abfrage scheitert.</summary>
    public static string? BinaryPath(ServiceHandle service)
    {
        // Laut Dokumentation höchstens 8 KB.
        const int size = 8192;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return QueryServiceConfigW(service, buffer, size, out _)
                ? Marshal.PtrToStringUni(Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer).lpBinaryPathName)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool ControlService(ServiceHandle service, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool QueryServiceStatus(ServiceHandle service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DeleteService(ServiceHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    /// <summary>Übersetzt die häufigsten Win32-Fehler beim Fernzugriff in verständliche Meldungen (englische Schlüssel; der Aufrufer lokalisiert).</summary>
    public static string Describe(int error) => error switch
    {
        5 => "Access denied – the account needs local administrator rights on the target computer (for local accounts additionally LocalAccountTokenFilterPolicy)",
        53 or 51 or 67 => "Computer or share ADMIN$ unreachable (check name, firewall, file and printer sharing)",
        86 or 1326 or 1327 or 1331 or 1909 => "Logon failed – user name or password wrong, account locked or disabled",
        1722 or 1753 => "RPC server unreachable – the target computer's Service Control Manager does not respond (check firewall)",
        _ => new Win32Exception(error).Message,
    };
}

internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ServiceHandle() : base(true) { }

    protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
}
