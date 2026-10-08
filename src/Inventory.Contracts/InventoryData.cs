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
}
