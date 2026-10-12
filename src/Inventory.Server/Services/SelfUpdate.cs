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

    /// <summary>Ordner neben dem Programm mit den Zertifikaten (*.cer), mit denen Releases signiert sein dürfen.</summary>
    public const string SigningFolder = "signing";

    private const uint TrustOk = 0;
    /// <summary>CERT_E_UNTRUSTEDROOT: die Signatur passt zur Datei, aber Windows vertraut dem Aussteller nicht (selbstsigniert).</summary>
    private const uint UntrustedRoot = 0x800B0109;

    /// <summary>
    /// Fingerabdrücke der Zertifikate, die diese Version mitbringt, plus die aus der Konfiguration. Sie kommen mit der
    /// installierten Version selbst – ein Update kann sie also erst ändern, nachdem es mit einem von ihnen geprüft wurde.
    /// </summary>
    public static IReadOnlyCollection<string> TrustedThumbprints(string baseDirectory, IEnumerable<string>? configured)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(baseDirectory, SigningFolder);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder, "*.cer"))
            {
                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
                    result.Add(certificate.Thumbprint);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Eine unlesbare Datei erweitert das Vertrauen nicht; die übrigen gelten weiter.
                }
            }
        }
        foreach (var thumbprint in configured ?? [])
        {
            var cleaned = new string(thumbprint.Where(Uri.IsHexDigit).ToArray());
            if (cleaned.Length == 40)
                result.Add(cleaned);
        }
        return result;
    }

    /// <summary>Prüft die Authenticode-Signatur einer Datei; liefert eine Fehlermeldung oder null.</summary>
    [SupportedOSPlatform("windows")]
    public static string? VerifySignature(string path, string? requiredPublisher, IReadOnlyCollection<string> trustedThumbprints)
    {
        var name = Path.GetFileName(path);
        var trust = VerifyTrust(path);
        if (trust is not (TrustOk or UntrustedRoot))
            return Decide(name, trust, null, null, requiredPublisher, trustedThumbprints);
        try
        {
            // Liefert das Signaturzertifikat; ob die Signatur zur Datei passt, hat WinVerifyTrust oben geprüft.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return Decide(name, trust, certificate.Thumbprint, certificate.GetNameInfo(X509NameType.SimpleName, false), requiredPublisher, trustedThumbprints);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return $"The signing certificate of {name} could not be read: {ex.Message}";
        }
    }

    /// <summary>
    /// Die Entscheidung ohne Windows-Aufrufe. Gültig ist eine Signatur, die zur Datei passt und entweder von einem der
    /// mitgelieferten oder in der Konfiguration zugelassenen Zertifikate stammt (auch selbstsigniert – die Konfiguration
    /// liegt in der Hand des Betreibers und ist für eigene, selbstsignierte Builds gedacht) oder von einem Herausgeber,
    /// dem Windows vertraut und der konfiguriert ist.
    /// </summary>
    public static string? Decide(string name, uint trust, string? thumbprint, string? subject, string? requiredPublisher, IReadOnlyCollection<string> trustedThumbprints)
    {
        if (trust is not (TrustOk or UntrustedRoot) || thumbprint is null)
            return $"The signature of {name} is invalid or missing (0x{trust:X8}).";
        if (trustedThumbprints.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
            return null;
        if (trust == TrustOk && !string.IsNullOrWhiteSpace(requiredPublisher))
            return subject is not null && subject.Contains(requiredPublisher, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"{name} is signed by \"{subject}\", expected \"{requiredPublisher}\".";
        return trust == UntrustedRoot
            ? $"{name} is signed with a certificate ({thumbprint}) that Windows does not trust and that is not one of the accepted release certificates."
            : $"{name} is signed by \"{subject}\" ({thumbprint}), which is not one of the accepted release certificates.";
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
