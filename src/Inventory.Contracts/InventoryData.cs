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

        public List<SoftwareItem> Software { get; set; } = new List<SoftwareItem>();
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

        /// <summary>Bereiche, die nicht gelesen werden konnten; der Rest des Scans ist trotzdem gültig.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
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
        /// <summary>WSUS-Server aus der Richtlinie; leer = Microsoft Update.</summary>
        public string? UpdateServer { get; set; }
    }

    public sealed class RemoteAccessInfo
    {
        /// <summary>Sitzungsnummer des angemeldeten Benutzers (für die RDP-Spiegelung); null, wenn niemand angemeldet ist.</summary>
        public int? SessionId { get; set; }
        public string? TeamViewerId { get; set; }
        public string? AnyDeskId { get; set; }
    }
}
