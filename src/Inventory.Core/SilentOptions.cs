using System.Text;

namespace Inventory.Core;

/// <param name="Installer">Erkannte Installer-Technik, z. B. "Inno Setup".</param>
/// <param name="Arguments">Empfohlene Parameter für die unbeaufsichtigte Installation; leer, wenn keine nötig sind.</param>
/// <param name="Note">Hinweis für die Oberfläche.</param>
public sealed record SilentSuggestion(string Installer, string Arguments, string Note);

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
                return new SilentSuggestion("Windows Installer (MSI)", "", "Läuft automatisch still mit /qn /norestart. Zusätzliche Eigenschaften wie ALLUSERS=1 oder TRANSFORMS=datei.mst können als Parameter angegeben werden.");
            case ".msp":
                return new SilentSuggestion("Windows-Installer-Patch (MSP)", "", "Läuft automatisch still mit /qn /norestart.");
            case ".msu":
                return new SilentSuggestion("Windows-Update (MSU)", "", "Läuft automatisch still mit /quiet /norestart.");
            case ".ps1" or ".cmd" or ".bat":
                return new SilentSuggestion("Skript", "", "Skripte laufen ohne Fenster; Parameter werden unverändert übergeben.");
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
                return new SilentSuggestion(installer, arguments, $"Erkannt als {installer}.");
        }
        return new SilentSuggestion("Unbekannter Installer", "",
            "Die Installer-Technik wurde nicht erkannt. Häufige Silent-Parameter sind /S, /silent, /quiet oder /qn – im Zweifel die Dokumentation des Herstellers prüfen.");
    }
}
