using System;
using System.Collections.Generic;

namespace Inventory.Contracts
{
    public enum StepType
    {
        Command,
        PowerShell,
        /// <summary>Hard- und Software des Zielrechners erfassen; das Ergebnis steht in <see cref="JobResult.Inventory"/>.</summary>
        InventoryScan,
        /// <summary>Installer aus den Paketdateien ausführen (msi, msp, msu, exe, cmd/bat, ps1).</summary>
        Install,
        /// <summary>Datei oder Ordner aus den Paketdateien auf den Zielrechner kopieren.</summary>
        FileCopy,
        /// <summary>Windows-Dienst starten, beenden oder neu starten.</summary>
        Service,
        Sleep,
        /// <summary>Neustart auslösen; nachfolgende Schritte werden nicht mehr ausgeführt.</summary>
        Reboot,
    }

    /// <summary>Unter welchem Konto ein Schritt auf dem Zielrechner läuft.</summary>
    public enum StepRunAs
    {
        /// <summary>Wie der Runner selbst: Lokales System (SMB, Agent) bzw. das hinterlegte Konto (WinRM).</summary>
        System,
        /// <summary>In der Sitzung des gerade angemeldeten Benutzers, mit dessen Rechten. Ist niemand angemeldet, wird der Schritt übersprungen.</summary>
        LoggedOnUser,
    }

    public enum ServiceAction
    {
        Start,
        Stop,
        Restart,
    }

    /// <summary>Auftrag an den Runner: liegt als <see cref="JobFiles.Manifest"/> im Job-Verzeichnis auf dem Zielrechner.</summary>
    public sealed class JobManifest
    {
        public string JobId { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>
        /// Gesetzt = der Runner führt nur aus, wenn der Rechner so heißt (NetBIOS-Name). Schützt davor, dass ein
        /// veralteter DNS-Eintrag den Job auf einen anderen Rechner lenkt.
        /// </summary>
        public string? ExpectedComputerName { get; set; }
        /// <summary>
        /// Fortsetzung nach einem Neustart: nur ausführen, wenn der Rechner seit dieser Betriebssystem-Startzeit (seine eigene
        /// Uhr, aus dem vorigen Ergebnis) neu gestartet ist. Sonst meldet der Runner <see cref="JobResult.NotRebootedYet"/>.
        /// </summary>
        public DateTime? RequireBootAfterUtc { get; set; }
        /// <summary>Woran zu erkennen ist, dass das Paket bereits installiert ist: alle Bedingungen müssen zutreffen.</summary>
        public StepConditions? Detection { get; set; }
        /// <summary>Trifft <see cref="Detection"/> schon vor dem ersten Schritt zu, wird nichts ausgeführt (<see cref="JobResult.AlreadyInstalled"/>).</summary>
        public bool SkipIfDetected { get; set; }
        /// <summary>Nach dem letzten Schritt muss <see cref="Detection"/> zutreffen, sonst gilt der Job als fehlgeschlagen.</summary>
        public bool VerifyAfterInstall { get; set; }
        public List<JobStep> Steps { get; set; } = new List<JobStep>();
    }

    public sealed class JobStep
    {
        public string Name { get; set; } = "";
        public StepType Type { get; set; }
        /// <summary>Befehlszeile (Command), Skripttext (PowerShell) bzw. Hinweistext beim Neustart.</summary>
        public string Script { get; set; } = "";
        /// <summary>Gilt für Command, PowerShell und Install.</summary>
        public StepRunAs RunAs { get; set; }
        /// <summary>Install/FileCopy: Pfad relativ zum Ordner <see cref="JobFiles.FilesFolder"/> des Jobs.</summary>
        public string? File { get; set; }
        /// <summary>Install: zusätzliche Parameter für den Installer.</summary>
        public string? Arguments { get; set; }
        /// <summary>FileCopy: Zielpfad auf dem Rechner; Umgebungsvariablen wie %ProgramFiles% werden aufgelöst.</summary>
        public string? Destination { get; set; }
        public string? ServiceName { get; set; }
        public ServiceAction ServiceAction { get; set; }
        /// <summary>Sleep: Wartezeit; Reboot: Verzögerung bis zum Neustart.</summary>
        public int Seconds { get; set; }
        public int TimeoutSeconds { get; set; } = 600;
        /// <summary>Leer = Standard des Schritt-Typs (0, bei Install zusätzlich 3010 und 1641).</summary>
        public List<int> SuccessCodes { get; set; } = new List<int>();
        /// <summary>Bei Fehler mit dem nächsten Schritt weitermachen statt abzubrechen.</summary>
        public bool ContinueOnError { get; set; }
        /// <summary>Trifft eine Bedingung nicht zu, wird der Schritt übersprungen (kein Fehler).</summary>
        public StepConditions? Conditions { get; set; }
    }

    public sealed class StepConditions
    {
        /// <summary>Mindestens diese Windows-Version, z. B. 10.0.22000 für Windows 11.</summary>
        public string? MinOsVersion { get; set; }
        /// <summary>Nur unterhalb dieser Windows-Version.</summary>
        public string? BelowOsVersion { get; set; }
        /// <summary>"x64" oder "x86"; leer = beide.</summary>
        public string? Architecture { get; set; }
        public string? FileExists { get; set; }
        public string? FileMissing { get; set; }
        /// <summary>Registry-Schlüssel wie HKLM\SOFTWARE\Firma\Produkt (64-Bit-Sicht).</summary>
        public string? RegistryKeyExists { get; set; }
        public string? RegistryKeyMissing { get; set; }
        /// <summary>true = nur wenn ein Benutzer angemeldet ist, false = nur wenn keiner angemeldet ist.</summary>
        public bool? UserLoggedOn { get; set; }
        /// <summary>
        /// Programm aus "Programme und Features": ohne * ein Teil des Anzeigenamens (ganze Wörter), mit * ein Muster für den ganzen Namen.
        /// </summary>
        public string? SoftwareInstalled { get; set; }
        /// <summary>Zusammen mit <see cref="SoftwareInstalled"/>: mindestens diese Version.</summary>
        public string? SoftwareMinVersion { get; set; }
        /// <summary>Kein Programm mit diesem Namen installiert (Schreibweise wie <see cref="SoftwareInstalled"/>).</summary>
        public string? SoftwareMissing { get; set; }
        /// <summary>MSI-Produktcode ({GUID}) eines installierten Produkts; trifft nur genau diese Version.</summary>
        public string? MsiProductCode { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(MinOsVersion) && string.IsNullOrWhiteSpace(BelowOsVersion) && string.IsNullOrWhiteSpace(Architecture)
            && string.IsNullOrWhiteSpace(FileExists) && string.IsNullOrWhiteSpace(FileMissing)
            && string.IsNullOrWhiteSpace(RegistryKeyExists) && string.IsNullOrWhiteSpace(RegistryKeyMissing) && UserLoggedOn == null
            && string.IsNullOrWhiteSpace(SoftwareInstalled) && string.IsNullOrWhiteSpace(SoftwareMissing) && string.IsNullOrWhiteSpace(MsiProductCode);
    }

    public static class JobFiles
    {
        public const string Manifest = "job.json";
        public const string Progress = "progress.jsonl";
        public const string Result = "result.json";
        /// <summary>Unterordner des Job-Verzeichnisses mit den Paketdateien; zugleich Arbeitsverzeichnis der Schritte.</summary>
        public const string FilesFolder = "files";
        public const string RunnerExe = "InventoryRunner.exe";
        /// <summary>Unterhalb von ADMIN$ (%SystemRoot%) auf dem Zielrechner.</summary>
        public const string RemoteRoot = "Inventory";
        public const string RunnerFolder = "Runner";
        public const string JobsFolder = "Jobs";
    }
}
