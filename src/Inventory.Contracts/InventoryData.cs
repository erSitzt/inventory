using System;
using System.Collections.Generic;

namespace Inventory.Contracts
{
    /// <summary>Ergebnis eines Inventar-Scans, wie es der Runner auf dem Zielrechner erfasst.</summary>
    public sealed class InventoryData
    {
        public string? ComputerName { get; set; }
        public string? Domain { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? SerialNumber { get; set; }
        public string? BiosVersion { get; set; }
        public string? OsName { get; set; }
        /// <summary>Vollständige Version inklusive Update-Stand, z. B. 10.0.26100.4349.</summary>
        public string? OsVersion { get; set; }
        /// <summary>Funktionsupdate, z. B. 24H2.</summary>
        public string? OsRelease { get; set; }
        public string? OsArchitecture { get; set; }
        public DateTime? OsInstalledUtc { get; set; }
        public DateTime? LastBootUtc { get; set; }
        public string? CpuName { get; set; }
        public int CpuCores { get; set; }
        public int CpuLogicalProcessors { get; set; }
        public long MemoryBytes { get; set; }
        public string? LoggedOnUser { get; set; }
        /// <summary>
        /// Benutzer mit einer interaktiven Sitzung (Konsole oder Remotedesktop) zum Zeitpunkt des Scans, als DOMÄNE\name.
        /// Dienste, geplante Aufgaben und Netzwerkanmeldungen zählen nicht. null bei Runnern, die das noch nicht erfassen.
        /// </summary>
        public List<string>? InteractiveUsers { get; set; }

        public List<SoftwareItem> Software { get; set; } = new List<SoftwareItem>();
        /// <summary>App-Pakete; null = der Runner hat sie nicht gelesen (ältere Version).</summary>
        public List<AppItem>? Apps { get; set; }
        public List<HotfixItem> Hotfixes { get; set; } = new List<HotfixItem>();
        public List<DiskItem> Disks { get; set; } = new List<DiskItem>();
        public List<NetworkAdapterItem> NetworkAdapters { get; set; } = new List<NetworkAdapterItem>();
        public List<ServiceItem> Services { get; set; } = new List<ServiceItem>();
        public List<LocalUserItem> LocalUsers { get; set; } = new List<LocalUserItem>();
        /// <summary>Mitglieder der lokalen Gruppe "Administratoren" (lokale Konten, Domänenkonten und -gruppen).</summary>
        public List<LocalAdminItem> LocalAdmins { get; set; } = new List<LocalAdminItem>();

        /// <summary>Firewall, Virenschutz und Windows Update; null bei Runnern, die das noch nicht erfassen.</summary>
        public SecurityStatus? Security { get; set; }

        /// <summary>Angaben für den Fernzugriff; null bei Runnern, die das noch nicht erfassen.</summary>
        public RemoteAccessInfo? RemoteAccess { get; set; }

        /// <summary>Hardware-Details und Plattformsicherheit; null bei Runnern, die das noch nicht erfassen.</summary>
        public HardwareInfo? Hardware { get; set; }

        /// <summary>Angaben für die Berechnung des Lizenzbedarfs; null bei Runnern, die das noch nicht erfassen.</summary>
        public LicensingInfo? Licensing { get; set; }

        /// <summary>Bereiche, die nicht gelesen werden konnten; der Rest des Scans ist trotzdem gültig.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        /// <summary>
        /// Benutzerprofile des Rechners und ob ihre Registry beim Scan geladen war. Nur aus geladenen Profilen sind pro Benutzer
        /// installierte Programme sichtbar; null = der Runner meldet das noch nicht.
        /// </summary>
        public List<UserProfileState>? UserProfiles { get; set; }
    }

    public sealed class UserProfileState
    {
        public string Sid { get; set; } = "";
        public bool Loaded { get; set; }
        /// <summary>DOMÄNE\\name; lässt sich die SID nicht auflösen, der Name des Profilordners. null bei Runnern, die das noch nicht erfassen.</summary>
        public string? Name { get; set; }
        /// <summary>Letzte Nutzung des Profils laut Windows.</summary>
        public DateTime? LastUseUtc { get; set; }
    }

    /// <summary>App-Paket (MSIX/AppX, auch aus dem Store), das auf dem Rechner registriert ist; ohne Bestandteile von Windows und Laufzeitbibliotheken.</summary>
    public sealed class AppItem
    {
        /// <summary>Name aus der Paketidentität, z. B. Microsoft.BingWeather; ihn erwartet der Entfernen-Schritt.</summary>
        public string Name { get; set; } = "";
        /// <summary>Anzeigename aus dem Manifest, soweit er dort im Klartext steht.</summary>
        public string? DisplayName { get; set; }
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? Architecture { get; set; }
        /// <summary>Für neue Benutzer vorgesehen (im Abbild bereitgestellt).</summary>
        public bool Provisioned { get; set; }
    }

    public sealed class SoftwareItem
    {
        public string Name { get; set; } = "";
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        /// <summary>Wie in der Registry abgelegt (meist yyyyMMdd).</summary>
        public string? InstallDate { get; set; }
        public bool Is64Bit { get; set; }
        /// <summary>true = nur für einen Benutzer installiert (HKCU).</summary>
        public bool PerUser { get; set; }
        public string? UninstallString { get; set; }
        /// <summary>Bei <see cref="PerUser"/>: SID des Benutzers, in dessen Profil das Programm eingetragen ist.</summary>
        public string? UserSid { get; set; }
        /// <summary>Setzt der Server: aus dem vorigen Scan übernommen, weil der Benutzer diesmal nicht angemeldet war.</summary>
        public bool CarriedOver { get; set; }
    }

    public sealed class HotfixItem
    {
        public string Id { get; set; } = "";
        public string? Description { get; set; }
        public string? InstalledOn { get; set; }
    }

    public sealed class DiskItem
    {
        public string Drive { get; set; } = "";
        public string? Label { get; set; }
        public string? FileSystem { get; set; }
        public long SizeBytes { get; set; }
        public long FreeBytes { get; set; }
    }

    public sealed class NetworkAdapterItem
    {
        public string Name { get; set; } = "";
        public string? MacAddress { get; set; }
        public List<string> IpAddresses { get; set; } = new List<string>();
        /// <summary>Je Adresse in <see cref="IpAddresses"/> die Subnetzmaske (IPv4) bzw. Präfixlänge (IPv6); leer bei älteren Runnern.</summary>
        public List<string> SubnetMasks { get; set; } = new List<string>();
        public List<string> Gateways { get; set; } = new List<string>();
        public List<string> DnsServers { get; set; } = new List<string>();
        public bool DhcpEnabled { get; set; }
    }

    public sealed class ServiceItem
    {
        public string Name { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? State { get; set; }
        public string? StartMode { get; set; }
        public string? Account { get; set; }
    }

    public sealed class LocalUserItem
    {
        public string Name { get; set; } = "";
        public string? FullName { get; set; }
        public string? Description { get; set; }
        public bool Disabled { get; set; }
        public string? Sid { get; set; }
        /// <summary>Alter des Kennworts in Tagen; null, wenn nicht ermittelt.</summary>
        public int? PasswordAgeDays { get; set; }
        /// <summary>Letzte Anmeldung an diesem Rechner; null = nie oder nicht ermittelt.</summary>
        public DateTime? LastLogonUtc { get; set; }
    }

    public sealed class LocalAdminItem
    {
        /// <summary>DOMÄNE\name bzw. RECHNER\name; bei nicht auflösbaren Konten die SID.</summary>
        public string Name { get; set; } = "";
        /// <summary>true = Gruppe, false = Benutzer.</summary>
        public bool IsGroup { get; set; }
        public string? Sid { get; set; }
    }

    /// <summary>Werte sind null, wenn sie sich auf dem Rechner nicht ermitteln ließen.</summary>
    /// <param name="Enabled">Das Produkt ist laut Sicherheitscenter eingeschaltet (Echtzeitschutz aktiv).</param>
    /// <param name="UpToDate">Die Definitionen sind laut Sicherheitscenter aktuell.</param>
    public sealed class AntivirusProductItem
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public bool UpToDate { get; set; }
    }

    public sealed class SecurityStatus
    {
        // Windows-Firewall je Netzwerkprofil (Gruppenrichtlinie berücksichtigt)
        public bool? FirewallDomain { get; set; }
        public bool? FirewallPrivate { get; set; }
        public bool? FirewallPublic { get; set; }

        // Microsoft Defender
        public bool? DefenderEnabled { get; set; }
        public bool? DefenderRealTime { get; set; }
        public bool? DefenderTamperProtected { get; set; }
        public string? DefenderSignatureVersion { get; set; }
        public DateTime? DefenderSignatureUpdatedUtc { get; set; }
        /// <summary>Betriebsart laut Defender: "Normal", "Passive Mode" (anderer Virenschutz aktiv), "EDR Block Mode", "SxS Passive Mode".</summary>
        public string? DefenderMode { get; set; }
        public DateTime? DefenderLastQuickScanUtc { get; set; }
        /// <summary>Beim Windows-Sicherheitscenter registrierte Virenschutzprogramme (nur Client-Betriebssysteme), Namen aller Produkte.</summary>
        public List<string> AntivirusProducts { get; set; } = new List<string>();
        /// <summary>Dieselben Produkte mit dem Zustand aus dem Sicherheitscenter.</summary>
        public List<AntivirusProductItem> AntivirusProductDetails { get; set; } = new List<AntivirusProductItem>();

        // Windows Update
        public DateTime? UpdateLastSearchUtc { get; set; }
        public DateTime? UpdateLastInstallUtc { get; set; }
        public bool? UpdateRebootRequired { get; set; }
        /// <summary>Ausstehende Updates laut letzter Suche des Rechners (es wird keine neue Online-Suche ausgelöst).</summary>
        public List<string>? UpdatePending { get; set; }
        /// <summary>Dieselben Updates mit KB-Nummern, Klassifizierung und Schweregrad; null bei Runnern, die das noch nicht erfassen.</summary>
        public List<UpdateItem>? UpdatePendingDetails { get; set; }
        /// <summary>WSUS-Server aus der Richtlinie; leer = Microsoft Update.</summary>
        public string? UpdateServer { get; set; }

        /// <summary>Sicherheitsrelevante Einstellungen des Betriebssystems; null bei Runnern, die das noch nicht erfassen.</summary>
        public SecurityPosture? Posture { get; set; }
    }

    public sealed class UpdateItem
    {
        public string Title { get; set; } = "";
        /// <summary>KB-Nummern mit Präfix, z. B. "KB5066835".</summary>
        public List<string> KbIds { get; set; } = new List<string>();
        /// <summary>Englische Bezeichnung der Klassifizierung, z. B. "Security Updates"; null, wenn unbekannt.</summary>
        public string? Classification { get; set; }
        /// <summary>Schweregrad laut MSRC: "Critical", "Important", "Moderate", "Low"; null, wenn keiner angegeben ist.</summary>
        public string? Severity { get; set; }
        public bool IsDownloaded { get; set; }
        public long SizeBytes { get; set; }
    }

    /// <summary>Jeder Wert ist null, wenn er sich bei diesem Scan nicht ermitteln ließ; der Server behält dann den vorigen.</summary>
    public sealed class SecurityPosture
    {
        /// <summary>Remotedesktop-Verbindungen sind erlaubt (Richtlinie berücksichtigt).</summary>
        public bool? RdpEnabled { get; set; }
        /// <summary>Remotedesktop verlangt die Anmeldung auf Netzwerkebene (NLA).</summary>
        public bool? RdpNla { get; set; }
        public bool? UacEnabled { get; set; }
        /// <summary>Stufe wie im Schieberegler der Systemsteuerung: 0 = nie benachrichtigen … 3 = immer benachrichtigen.</summary>
        public int? UacLevel { get; set; }
        /// <summary>SMB 1 ist als Server oder Client nutzbar.</summary>
        public bool? Smb1Enabled { get; set; }
        public bool? CredentialGuard { get; set; }
        /// <summary>Speicherintegrität (HVCI) läuft.</summary>
        public bool? MemoryIntegrity { get; set; }
        /// <summary>LSA läuft als geschützter Prozess (RunAsPPL).</summary>
        public bool? LsaProtection { get; set; }
        /// <summary>Das vordefinierte Administratorkonto (RID 500) ist aktiv.</summary>
        public bool? BuiltinAdminEnabled { get; set; }
        public int? BuiltinAdminPasswordAgeDays { get; set; }
        public bool? WindowsActivated { get; set; }
    }

    public sealed class RemoteAccessInfo
    {
        /// <summary>Sitzungsnummer des angemeldeten Benutzers (für die RDP-Spiegelung); null, wenn niemand angemeldet ist.</summary>
        public int? SessionId { get; set; }
        public string? TeamViewerId { get; set; }
        public string? AnyDeskId { get; set; }
    }

    /// <summary>Werte sind null bzw. Listen leer, wenn sie sich auf dem Rechner nicht ermitteln ließen.</summary>
    public sealed class HardwareInfo
    {
        /// <summary>
        /// Bereiche (Konstanten aus <see cref="HardwareAreas"/>), die sich bei diesem Scan nicht lesen ließen. Ihre Werte sind
        /// leer, aber nicht "nicht vorhanden": der Server behält dafür den vorigen Stand.
        /// </summary>
        public List<string> Failed { get; set; } = new List<string>();

        /// <summary>Gehäusetyp laut SMBIOS (Win32_SystemEnclosure.ChassisTypes, erster Eintrag), z. B. 3 = Desktop, 10 = Notebook.</summary>
        public int? ChassisType { get; set; }
        /// <summary>"UEFI" oder "BIOS".</summary>
        public string? FirmwareType { get; set; }
        public bool? SecureBoot { get; set; }
        public DateTime? BiosDateUtc { get; set; }

        public bool? TpmPresent { get; set; }
        /// <summary>Spezifikationsversion, z. B. "2.0".</summary>
        public string? TpmVersion { get; set; }
        /// <summary>Eingeschaltet, aktiviert und in Besitz genommen.</summary>
        public bool? TpmReady { get; set; }

        public List<GpuItem> Gpus { get; set; } = new List<GpuItem>();
        /// <summary>Anzahl der Speichersteckplätze der Hauptplatine.</summary>
        public int? MemorySlots { get; set; }
        public List<MemoryModuleItem> MemoryModules { get; set; } = new List<MemoryModuleItem>();
        public List<PhysicalDiskItem> PhysicalDisks { get; set; } = new List<PhysicalDiskItem>();
        public List<VolumeEncryptionItem> Volumes { get; set; } = new List<VolumeEncryptionItem>();
        public List<MonitorItem> Monitors { get; set; } = new List<MonitorItem>();
        public List<PrinterItem> Printers { get; set; } = new List<PrinterItem>();

        /// <summary>false = kein Akku verbaut.</summary>
        public bool? BatteryPresent { get; set; }
        /// <summary>Volle Ladung im Verhältnis zur Kapazität im Neuzustand, in Prozent.</summary>
        public int? BatteryHealthPercent { get; set; }
        public int? BatteryCycleCount { get; set; }
    }

    public static class HardwareAreas
    {
        public const string Firmware = "Firmware";
        public const string Tpm = "Tpm";
        public const string Graphics = "Graphics";
        public const string Memory = "Memory";
        public const string Disks = "Disks";
        public const string Volumes = "Volumes";
        public const string Battery = "Battery";
        public const string Monitors = "Monitors";
        public const string Printers = "Printers";
    }

    public sealed class GpuItem
    {
        public string Name { get; set; } = "";
        public string? DriverVersion { get; set; }
    }

    public sealed class MemoryModuleItem
    {
        /// <summary>Bezeichnung des Steckplatzes, z. B. "DIMM 1".</summary>
        public string Slot { get; set; } = "";
        public string? Manufacturer { get; set; }
        public string? PartNumber { get; set; }
        public string? SerialNumber { get; set; }
        public long CapacityBytes { get; set; }
        public int? SpeedMhz { get; set; }
        /// <summary>Z. B. "DDR4".</summary>
        public string? Type { get; set; }
    }

    public sealed class PhysicalDiskItem
    {
        public string Model { get; set; } = "";
        public string? SerialNumber { get; set; }
        /// <summary>"SSD", "HDD" oder null, wenn Windows es nicht weiß.</summary>
        public string? MediaType { get; set; }
        /// <summary>Anschluss, z. B. "NVMe", "SATA", "USB".</summary>
        public string? BusType { get; set; }
        public long SizeBytes { get; set; }
        /// <summary>"Healthy", "Warning" oder "Unhealthy".</summary>
        public string? Health { get; set; }
    }

    public sealed class VolumeEncryptionItem
    {
        public string Drive { get; set; } = "";
        /// <summary>BitLocker-Schutz ist eingeschaltet; null = Zustand unbekannt (z. B. gesperrtes Laufwerk).</summary>
        public bool? Protected { get; set; }
        /// <summary>Anteil der verschlüsselten Daten in Prozent.</summary>
        public int? EncryptedPercent { get; set; }
        /// <summary>Z. B. "XTS-AES 256".</summary>
        public string? Method { get; set; }
    }

    public sealed class MonitorItem
    {
        public string? Manufacturer { get; set; }
        public string Model { get; set; } = "";
        public string? SerialNumber { get; set; }
        public int? Year { get; set; }
    }

    public sealed class PrinterItem
    {
        public string Name { get; set; } = "";
        public string? Driver { get; set; }
        public string? Port { get; set; }
        public bool Network { get; set; }
        public bool Shared { get; set; }
    }

    /// <summary>
    /// Was die Lizenzberechnung über den Rechner wissen muss: Sockel und Kerne, ob er virtuell ist und wo er läuft, Edition und
    /// Lizenzkanal, Serverrollen, SQL-Server-Instanzen und, auf einem Hyper-V-Host, seine virtuellen Maschinen.
    /// Jeder Wert ist null, wenn er sich nicht lesen ließ.
    /// </summary>
    public sealed class LicensingInfo
    {
        /// <summary>Anzahl der Prozessorsockel (Instanzen von Win32_Processor).</summary>
        public int? CpuSockets { get; set; }
        /// <summary>Physische Kerne des größten Prozessors; Hyperthreading zählt nicht.</summary>
        public int? CpuCoresPerSocket { get; set; }
        /// <summary>UUID aus dem SMBIOS (Win32_ComputerSystemProduct); bei einer VM dieselbe, die der Hypervisor für sie führt.</summary>
        public string? BiosUuid { get; set; }
        /// <summary>Win32_ComputerSystem.HypervisorPresent: auf einer VM und auf einem Host mit aktivem Hypervisor true.</summary>
        public bool? HypervisorPresent { get; set; }
        /// <summary>true, wenn der Rechner sich als Hyper-V-Gast zu erkennen gibt (Gastdienste mit Host-Angaben).</summary>
        public bool? HyperVGuest { get; set; }
        /// <summary>Der Hyper-V-Host laut Gastdiensten (PhysicalHostNameFullyQualified, sonst PhysicalHostName).</summary>
        public string? ReportedHostName { get; set; }
        /// <summary>Win32_OperatingSystem.OperatingSystemSKU, z. B. 7 = Server Standard, 8 = Server Datacenter.</summary>
        public int? OsSku { get; set; }
        /// <summary>Win32_OperatingSystem.ProductType: 1 = Arbeitsplatz, 2 = Domänencontroller, 3 = Server.</summary>
        public int? OsProductType { get; set; }
        /// <summary>EditionID aus der Registry, z. B. ServerDatacenter, Professional, EnterpriseS.</summary>
        public string? OsEdition { get; set; }
        /// <summary>
        /// Lizenzkanal der aktiven Windows-Lizenz, z. B. OEM:DM, OEM:SLP, Retail, Volume:MAK, Volume:GVLK;
        /// auf älteren Systemen aus der Beschreibung abgeleitet (OEM, Retail, Volume:MAK, Volume:KMS).
        /// </summary>
        public string? LicenseChannel { get; set; }
        /// <summary>Installierte Serverrollen und -features (Win32_ServerFeature); null auf Arbeitsplatzsystemen.</summary>
        public List<ServerFeatureItem>? ServerFeatures { get; set; }
        /// <summary>SQL-Server-Instanzen laut Registry.</summary>
        public List<SqlInstanceItem>? SqlInstances { get; set; }
        /// <summary>Name des Failover-Clusters, dem der Rechner angehört.</summary>
        public string? ClusterName { get; set; }
        /// <summary>Virtuelle Maschinen, wenn der Rechner ein Hyper-V-Host ist; null, wenn nicht oder nicht lesbar.</summary>
        public List<HostedVmItem>? VirtualMachines { get; set; }
    }

    public sealed class ServerFeatureItem
    {
        /// <summary>ID laut Win32_ServerFeature, z. B. 20 = Hyper-V, 130 = Remote Desktop Session Host.</summary>
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class SqlInstanceItem
    {
        /// <summary>Name der Instanz, z. B. MSSQLSERVER oder SQLEXPRESS.</summary>
        public string Name { get; set; } = "";
        /// <summary>Interner Schlüssel, z. B. MSSQL16.MSSQLSERVER.</summary>
        public string? InstanceId { get; set; }
        /// <summary>Edition laut Setup, z. B. "Enterprise Edition: Core-based Licensing (64-bit)".</summary>
        public string? Edition { get; set; }
        public string? Version { get; set; }
        public string? PatchLevel { get; set; }
        /// <summary>Die Instanz ist eine Failover-Cluster-Instanz.</summary>
        public bool Clustered { get; set; }
    }

    public sealed class HostedVmItem
    {
        public string Name { get; set; } = "";
        /// <summary>BIOS-GUID der VM, wie sie der Gast als SMBIOS-UUID meldet.</summary>
        public string? BiosUuid { get; set; }
        public int? VirtualCpus { get; set; }
        public bool Running { get; set; }
    }
}
