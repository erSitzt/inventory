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
        /// <summary>Installiertes Programm still deinstallieren (Name oder MSI-Produktcode); ist es nicht vorhanden, gilt der Schritt als erledigt.</summary>
        Uninstall = 8,
        /// <summary>Ausstehende Windows-Updates suchen, laden und installieren; Exitcode 3010, wenn danach ein Neustart nötig ist.</summary>
        WindowsUpdate = 9,
        /// <summary>
        /// Wake-on-LAN-Pakete in die eigenen Netze schicken; die MAC-Adressen stehen durch Komma getrennt in
        /// <see cref="JobStep.Script"/>. Kein Schritt für Pakete: der Server schickt ihn an einen Agenten als Weckrelais.
        /// </summary>
        WakeOnLan = 10,
        /// <summary>Registrierungswert setzen oder löschen bzw. einen Schlüssel löschen.</summary>
        Registry = 11,
        /// <summary>Optionale Windows-Features ein- oder ausschalten (DISM); Exitcode 3010, wenn danach ein Neustart nötig ist.</summary>
        WindowsFeature = 12,
        /// <summary>App-Paket (MSIX/AppX) für alle Benutzer entfernen, auf Wunsch samt Bereitstellung für neue Benutzer.</summary>
        RemoveApp = 13,
    }

    public enum RegistryAction
    {
        SetValue,
        DeleteValue,
        /// <summary>Den Schlüssel samt Unterschlüsseln löschen.</summary>
        DeleteKey,
    }

    public enum RegistryDataType
    {
        String,
        ExpandString,
        DWord,
        QWord,
        /// <summary>Mehrere Zeilen; je Zeile ein Eintrag.</summary>
        MultiString,
        /// <summary>Hexadezimal, z. B. "01 0A FF".</summary>
        Binary,
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
        /// <summary>Voraussetzungen des Pakets: ihre Schritte tragen die Nummer in <see cref="JobStep.Group"/>.</summary>
        public List<JobGroup> Groups { get; set; } = new List<JobGroup>();
        /// <summary>
        /// Fortsetzung nach einem Neustart mitten in einer Voraussetzung: diese Gruppen laufen ohne erneute Erkennung weiter,
        /// weil die Voraussetzung nach dem Neustart oft schon erkennbar ist, obwohl noch Schritte ausstehen.
        /// </summary>
        public List<int> StartedGroups { get; set; } = new List<int>();
        /// <summary>
        /// Fortsetzung nach einem Neustart, der der letzte Schritt einer Voraussetzung war: diese Gruppen prüft der Folgejob
        /// vor seinem ersten Schritt nach (<see cref="JobGroup.Verify"/>).
        /// </summary>
        public List<int> FinishedGroups { get; set; } = new List<int>();
    }

    /// <summary>Schritte einer Voraussetzung: sie entfallen gemeinsam, wenn die Voraussetzung schon vorhanden ist.</summary>
    public sealed class JobGroup
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Trifft dies vor dem ersten Schritt der Gruppe zu, werden ihre Schritte übersprungen; null = immer ausführen.</summary>
        public StepConditions? Detection { get; set; }
        /// <summary>Nach dem letzten Schritt der Gruppe muss <see cref="Detection"/> zutreffen, sonst scheitert der Job.</summary>
        public bool Verify { get; set; }
        /// <summary>Voraussetzung einer Voraussetzung: ist die äußere vorhanden, entfällt auch diese.</summary>
        public int? Parent { get; set; }
    }

    public sealed class JobStep
    {
        public string Name { get; set; } = "";
        public StepType Type { get; set; }
        /// <summary>Nur im Job gesetzt: der Schritt gehört zu dieser Voraussetzung (<see cref="JobGroup.Id"/>).</summary>
        public int? Group { get; set; }
        /// <summary>Befehlszeile (Command), Skripttext (PowerShell) bzw. Hinweistext beim Neustart.</summary>
        public string Script { get; set; } = "";
        /// <summary>Gilt für Command, PowerShell und Install.</summary>
        public StepRunAs RunAs { get; set; }
        /// <summary>Install/FileCopy: Pfad relativ zum Ordner <see cref="JobFiles.FilesFolder"/> des Jobs.</summary>
        public string? File { get; set; }
        /// <summary>Uninstall: Name des Programms, Schreibweise wie bei <see cref="StepConditions.SoftwareInstalled"/>.</summary>
        public string? SoftwareName { get; set; }
        /// <summary>Uninstall: MSI-Produktcode; hat Vorrang vor <see cref="SoftwareName"/>.</summary>
        public string? ProductCode { get; set; }
        /// <summary>Install: zusätzliche Parameter für den Installer. Uninstall: Parameter für die stille Deinstallation, falls der Runner sie nicht selbst kennt.</summary>
        public string? Arguments { get; set; }
        /// <summary>FileCopy: Zielpfad auf dem Rechner; Umgebungsvariablen wie %ProgramFiles% werden aufgelöst.</summary>
        public string? Destination { get; set; }
        /// <summary>
        /// FileCopy: Quelle außerhalb des Pakets – ein UNC-Pfad oder ein Pfad auf dem Zielrechner; hat Vorrang vor <see cref="File"/>.
        /// Für sehr große Dateien, die nicht über den Server laufen sollen. Der Runner liest mit seinem eigenen Konto.
        /// </summary>
        public string? SourcePath { get; set; }
        /// <summary>Registry: Schlüssel mit Struktur, z. B. HKLM\SOFTWARE\Firma\Programm. HKCU gilt für den angemeldeten Benutzer (<see cref="RunAs"/>).</summary>
        public string? RegistryKey { get; set; }
        /// <summary>Registry: Name des Werts; leer = der Standardwert des Schlüssels.</summary>
        public string? RegistryValueName { get; set; }
        public RegistryAction RegistryAction { get; set; }
        public RegistryDataType RegistryDataType { get; set; }
        /// <summary>Registry: der Wert als Text, passend zu <see cref="RegistryDataType"/>.</summary>
        public string? RegistryData { get; set; }
        /// <summary>Registry: die 32-Bit-Ansicht (WOW6432Node) statt der 64-Bit-Ansicht.</summary>
        public bool Registry32Bit { get; set; }
        /// <summary>WindowsFeature: Namen der Features wie bei DISM, z. B. NetFx3 oder TelnetClient.</summary>
        public List<string> FeatureNames { get; set; } = new List<string>();
        /// <summary>RemoveApp: Name aus der Paketidentität, z. B. Microsoft.BingWeather; * als Platzhalter.</summary>
        public string? AppName { get; set; }
        /// <summary>RemoveApp: die Bereitstellung für neue Benutzer behalten; sonst bekäme jeder neue Benutzer die App wieder.</summary>
        public bool AppKeepProvisioned { get; set; }
        /// <summary>WindowsFeature: ausschalten statt einschalten.</summary>
        public bool FeatureDisable { get; set; }
        public string? ServiceName { get; set; }
        public ServiceAction ServiceAction { get; set; }
        /// <summary>Sleep: Wartezeit; Reboot: Verzögerung bis zum Neustart.</summary>
        public int Seconds { get; set; }
        /// <summary>WindowsUpdate: nur Sicherheits- und kritische Updates sowie solche mit MSRC-Schweregrad.</summary>
        public bool UpdateSecurityOnly { get; set; }
        /// <summary>WindowsUpdate: auch Treiber, die Windows Update anbietet.</summary>
        public bool UpdateIncludeDrivers { get; set; }
        /// <summary>WindowsUpdate: höchstens so viele Updates je Lauf; 0 = alle.</summary>
        public int UpdateMaxCount { get; set; }
        /// <summary>WindowsUpdate: nur diese KB-Nummern installieren; leer = alles, was die übrigen Einstellungen zulassen.</summary>
        public List<string> UpdateKbIds { get; set; } = new List<string>();
        /// <summary>
        /// Gilt wie angegeben, auch für lang laufende Schritt-Typen: die Oberfläche schlägt je Typ einen passenden Wert vor
        /// (Install/Uninstall eine Stunde, WindowsUpdate zwei), über die API angelegte Schritte müssen ihn selbst setzen.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 600;
        /// <summary>Leer = Standard des Schritt-Typs (0, bei Install zusätzlich 3010 und 1641).</summary>
        public List<int> SuccessCodes { get; set; } = new List<int>();
        /// <summary>Bei Fehler mit dem nächsten Schritt weitermachen statt abzubrechen.</summary>
        public bool ContinueOnError { get; set; }
        /// <summary>
        /// So oft wird ein fehlgeschlagener Schritt wiederholt (höchstens 20). Unabhängig davon wiederholt der Runner Install und
        /// Uninstall bis zu dreimal, wenn gerade eine andere Installation läuft (Exitcode 1618). Scheitert schon die Vorbereitung
        /// (zu wenig Speicherplatz, Programm lässt sich nicht schließen), wird nicht wiederholt: das erledigt sich nicht von selbst.
        /// </summary>
        public int Retries { get; set; }
        /// <summary>Pause vor jeder Wiederholung, höchstens eine Stunde.</summary>
        public int RetryDelaySeconds { get; set; } = 60;
        /// <summary>
        /// So viel muss auf dem Systemlaufwerk frei sein, sonst schlägt der Schritt fehl, bevor er beginnt.
        /// 0 = bei Install das Doppelte der Installationsdatei, sonst keine Prüfung.
        /// </summary>
        public int MinFreeDiskMb { get; set; }
        /// <summary>Prozessnamen (mit oder ohne .exe), die vor dem Schritt beendet werden, z. B. damit ein Setup keine Dateien in Benutzung vorfindet.</summary>
        public List<string> CloseProcesses { get; set; } = new List<string>();
        /// <summary>Größer 0: läuft eines der Programme und ist jemand angemeldet, erscheint zuerst ein Hinweis; beendet wird nach dieser Frist (höchstens eine Stunde).</summary>
        public int CloseProcessesWarnSeconds { get; set; }
        /// <summary>Text des Hinweises; leer = Standardtext des Runners.</summary>
        public string? CloseProcessesMessage { get; set; }
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
        /// <summary>true = nur wenn ein Neustart aussteht (z. B. nach Updates), false = nur wenn keiner aussteht.</summary>
        public bool? RebootPending { get; set; }
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
            && string.IsNullOrWhiteSpace(RegistryKeyExists) && string.IsNullOrWhiteSpace(RegistryKeyMissing) && UserLoggedOn == null && RebootPending == null
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
        /// <summary>
        /// Fester Name des Dienstes, über den der SMB-Transport Jobs startet. Der Dienst bleibt auf dem Zielrechner bestehen
        /// und bekommt die Job-ID beim Start übergeben; so entsteht nicht je Job ein neuer Dienst.
        /// </summary>
        public const string RunnerServiceName = "InventoryRunner";

        /// <summary>Job-IDs sind zugleich Ordnernamen; alles andere wird abgelehnt.</summary>
        public static bool IsJobId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
                return false;
            foreach (var c in value)
            {
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_')
                    return false;
            }
            return true;
        }
    }
}
