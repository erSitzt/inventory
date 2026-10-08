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
    public static extern bool StartServiceW(ServiceHandle service, int argc, IntPtr argv);

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

    /// <summary>Übersetzt die häufigsten Win32-Fehler beim Fernzugriff in verständliche Meldungen.</summary>
    public static string Describe(int error) => error switch
    {
        5 => "Zugriff verweigert – das Konto braucht lokale Administratorrechte auf dem Zielrechner (bei lokalen Konten zusätzlich LocalAccountTokenFilterPolicy)",
        53 or 51 or 67 => "Rechner oder Freigabe ADMIN$ nicht erreichbar (Name, Firewall, Datei- und Druckerfreigabe prüfen)",
        86 or 1326 or 1327 or 1331 or 1909 => "Anmeldung fehlgeschlagen – Benutzername oder Passwort falsch, Konto gesperrt oder deaktiviert",
        1722 or 1753 => "RPC-Server nicht erreichbar – der Dienststeuerungs-Manager des Zielrechners antwortet nicht (Firewall prüfen)",
        _ => new Win32Exception(error).Message,
    };
}

internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ServiceHandle() : base(true) { }

    protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
}
