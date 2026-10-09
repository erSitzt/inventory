using System.Text;

namespace Inventory.Core;

/// <param name="Installer">Erkannte Installer-Technik, z. B. "Inno Setup" (englischer Text; die Oberfläche übersetzt).</param>
/// <param name="Arguments">Empfohlene Parameter für die unbeaufsichtigte Installation; leer, wenn keine nötig sind.</param>
/// <param name="Note">Hinweis für die Oberfläche.</param>
/// <param name="Known">false, wenn die Installer-Technik nicht erkannt wurde.</param>
public sealed record SilentSuggestion(string Installer, string Arguments, string Note, bool Known = true);

/// <summary>Erkennt gängige Installer-Techniken an der Datei und schlägt die passenden Silent-Parameter vor.</summary>
public static class SilentOptions
{
    /// <summary>Die Kennungen stehen im Programmkopf bzw. im eingebetteten Manifest, also am Dateianfang.</summary>
    private const int ScanBytes = 4 * 1024 * 1024;

    private static readonly (string Signature, string Installer, string Arguments)[] Signatures =
    [
        // Reihenfolge zählt: spezifische Kennungen zuerst, da z. B. InstallShield-Pakete oft auch "Windows Installer" enthalten.
        (".wixburn", "WiX Burn", "/quiet /norestart"),
        ("Inno Setup", "Inno Setup", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"),
        ("Nullsoft", "NSIS", "/S"),
        ("InstallShield", "InstallShield", "/s /v\"/qn /norestart\""),
        ("Advanced Installer", "Advanced Installer", "/exenoui /qn /norestart"),
        ("InstallAware", "InstallAware", "/s"),
        ("SquirrelTemp", "Squirrel", "--silent"),
        ("WiseMain", "Wise", "/s"),
        ("7zS.sfx", "7-Zip-Selbstentpacker", "/S"),
    ];

    public static SilentSuggestion? Detect(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".msi":
                return new SilentSuggestion("Windows Installer (MSI)", "", "Runs silently with /qn /norestart automatically. Additional properties such as ALLUSERS=1 or TRANSFORMS=file.mst can be given as parameters.");
            case ".msp":
                return new SilentSuggestion("Windows Installer patch (MSP)", "", "Runs silently with /qn /norestart automatically.");
            case ".msu":
                return new SilentSuggestion("Windows Update (MSU)", "", "Runs silently with /quiet /norestart automatically.");
            case ".ps1" or ".cmd" or ".bat":
                return new SilentSuggestion("Script", "", "Scripts run without a window; parameters are passed unchanged.");
            case ".exe":
                break;
            default:
                return null;
        }

        var buffer = new byte[ScanBytes];
        int length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var data = buffer.AsSpan(0, length);

        foreach (var (signature, installer, arguments) in Signatures)
        {
            // Kennungen stehen je nach Stelle als ANSI (Abschnittsnamen, Manifest) oder UTF-16 (Versionsinfo) in der Datei.
            if (data.IndexOf(Encoding.ASCII.GetBytes(signature)) >= 0 || data.IndexOf(Encoding.Unicode.GetBytes(signature)) >= 0)
                return new SilentSuggestion(installer, arguments, "Recognized by its signature.");
        }
        return new SilentSuggestion("Unknown installer", "",
            "The installer technology was not recognized. Common silent parameters are /S, /silent, /quiet or /qn – when in doubt, check the vendor's documentation.", Known: false);
    }
}
