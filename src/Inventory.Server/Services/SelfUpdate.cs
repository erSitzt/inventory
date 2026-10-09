using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Inventory.Server.Services;

/// <summary>Der Windows-spezifische Teil der Selbstaktualisierung: Voraussetzungen prüfen, Setup starten, Signatur prüfen.</summary>
public static class SelfUpdate
{
    /// <summary>Legt das Setup im Programmordner ab; daran ist eine Installation per Setup zu erkennen.</summary>
    private const string UninstallerName = "unins000.exe";

    /// <summary>null, wenn sich diese Instanz selbst aktualisieren kann; sonst der Grund, warum nicht.</summary>
    public static string? CannotInstallReason()
    {
        if (!OperatingSystem.IsWindows())
            return "Self-update is only available on Windows.";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, UninstallerName)))
            return "This instance was not installed with the setup and therefore cannot update itself.";
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            ? null
            : $"The service account {identity.Name} has no local administrator rights; the setup has to be run manually.";
    }

    /// <summary>Startet das Setup unbeaufsichtigt. Es beendet diesen Dienst, ersetzt die Dateien und startet ihn neu.</summary>
    public static void Launch(string setup, string log)
    {
        using var process = Process.Start(new ProcessStartInfo(setup)
        {
            ArgumentList = { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", $"/LOG={log}" },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>Prüft, dass die Datei eine gültige Authenticode-Signatur des erwarteten Herausgebers trägt; liefert eine Fehlermeldung oder null.</summary>
    [SupportedOSPlatform("windows")]
    public static string? VerifySignature(string path, string requiredPublisher)
    {
        var trust = VerifyTrust(path);
        if (trust != 0)
            return $"The signature of {Path.GetFileName(path)} is invalid or missing (0x{trust:X8}).";
        string subject;
        try
        {
            // Liefert das Signaturzertifikat; ob die Signatur zur Datei passt, hat WinVerifyTrust oben geprüft.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            subject = certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return $"The signing certificate of {Path.GetFileName(path)} could not be read: {ex.Message}";
        }
        return subject.Contains(requiredPublisher, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{Path.GetFileName(path)} is signed by \"{subject}\", expected \"{requiredPublisher}\".";
    }

    [SupportedOSPlatform("windows")]
    private static uint VerifyTrust(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE: der Server soll auch ohne Internetzugang zur Sperrliste prüfen können
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 0,
                dwProvFlags = 0x00000010 | 0x00001000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
