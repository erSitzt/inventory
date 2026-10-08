using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    /// <summary>Erfasst Hard- und Software lokal über WMI und Registry.</summary>
    internal static class InventoryCollector
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static InventoryData Collect()
        {
            var data = new InventoryData();
            Section(data, "System", () => ReadSystem(data));
            Section(data, "Betriebssystem", () => ReadOperatingSystem(data));
            Section(data, "Prozessor", () => ReadProcessor(data));
            Section(data, "Software", () => ReadSoftware(data));
            Section(data, "Hotfixes", () => ReadHotfixes(data));
            Section(data, "Datenträger", () => ReadDisks(data));
            Section(data, "Netzwerk", () => ReadNetwork(data));
            Section(data, "Dienste", () => ReadServices(data));
            Section(data, "Lokale Benutzer", () => ReadLocalUsers(data));
            Section(data, "Lokale Administratoren", () => ReadLocalAdmins(data));
            return data;
        }

        private static void Section(InventoryData data, string name, Action read)
        {
            try
            {
                read();
            }
            catch (Exception ex)
            {
                data.Warnings.Add($"{name}: {ex.Message}");
            }
        }

        private static void ReadSystem(InventoryData data)
        {
            foreach (var o in Query("SELECT Name, Domain, Manufacturer, Model, TotalPhysicalMemory, UserName FROM Win32_ComputerSystem"))
            {
                data.ComputerName = Text(o, "Name");
                data.Domain = Text(o, "Domain");
                data.Manufacturer = Text(o, "Manufacturer");
                data.Model = Text(o, "Model");
                data.MemoryBytes = Number(o, "TotalPhysicalMemory");
                data.LoggedOnUser = Text(o, "UserName");
            }
            foreach (var o in Query("SELECT SerialNumber, SMBIOSBIOSVersion FROM Win32_BIOS"))
            {
                data.SerialNumber = Text(o, "SerialNumber");
                data.BiosVersion = Text(o, "SMBIOSBIOSVersion");
            }
        }

        private static void ReadOperatingSystem(InventoryData data)
        {
            foreach (var o in Query("SELECT Caption, Version, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                data.OsName = Text(o, "Caption");
                data.OsVersion = Text(o, "Version");
                data.OsArchitecture = Text(o, "OSArchitecture");
                data.OsInstalledUtc = Date(o, "InstallDate");
                data.LastBootUtc = Date(o, "LastBootUpTime");
            }

            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key == null)
                    return;
                data.OsRelease = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
                // Win32_OperatingSystem liefert den Update-Stand (UBR) nicht mit.
                if (key.GetValue("UBR") is int ubr && data.OsVersion != null)
                    data.OsVersion += "." + ubr;
            }
        }

        private static void ReadProcessor(InventoryData data)
        {
            foreach (var o in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                data.CpuName = data.CpuName ?? Text(o, "Name");
                data.CpuCores += (int)Number(o, "NumberOfCores");
                data.CpuLogicalProcessors += (int)Number(o, "NumberOfLogicalProcessors");
            }
        }

        private static void ReadSoftware(InventoryData data)
        {
            using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                ReadUninstall(data, hklm64.OpenSubKey(UninstallKey), is64Bit: Environment.Is64BitOperatingSystem, perUser: false);
            if (Environment.Is64BitOperatingSystem)
            {
                using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    ReadUninstall(data, hklm32.OpenSubKey(UninstallKey), is64Bit: false, perUser: false);
            }

            // Pro Benutzer installierte Programme; sichtbar sind nur die Profile, deren Registry gerade geladen ist.
            using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            {
                foreach (var sid in users.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !n.EndsWith("_Classes", StringComparison.Ordinal)))
                    ReadUninstall(data, users.OpenSubKey(sid + "\\" + UninstallKey), is64Bit: Environment.Is64BitOperatingSystem, perUser: true);
            }

            data.Software = data.Software
                .GroupBy(s => (s.Name, s.Version, s.Is64Bit, s.PerUser))
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void ReadUninstall(InventoryData data, RegistryKey? uninstall, bool is64Bit, bool perUser)
        {
            if (uninstall == null)
                return;
            using (uninstall)
            {
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using (var key = uninstall.OpenSubKey(name))
                    {
                        // Dieselben Regeln wie "Programme und Features": ohne Anzeigenamen, Systemkomponenten und Unterpakete ausblenden.
                        if (!(key?.GetValue("DisplayName") is string displayName) || string.IsNullOrWhiteSpace(displayName))
                            continue;
                        if (key.GetValue("SystemComponent") is int system && system == 1)
                            continue;
                        if (key.GetValue("ParentKeyName") != null)
                            continue;

                        data.Software.Add(new SoftwareItem
                        {
                            Name = displayName.Trim(),
                            Version = (key.GetValue("DisplayVersion") as string)?.Trim(),
                            Publisher = (key.GetValue("Publisher") as string)?.Trim(),
                            InstallDate = key.GetValue("InstallDate") as string,
                            UninstallString = key.GetValue("UninstallString") as string,
                            Is64Bit = is64Bit,
                            PerUser = perUser,
                        });
                    }
                }
            }
        }

        private static void ReadHotfixes(InventoryData data)
        {
            foreach (var o in Query("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering"))
            {
                var id = Text(o, "HotFixID");
                if (!string.IsNullOrEmpty(id))
                    data.Hotfixes.Add(new HotfixItem { Id = id!, Description = Text(o, "Description"), InstalledOn = Text(o, "InstalledOn") });
            }
        }

        private static void ReadDisks(InventoryData data)
        {
            foreach (var o in Query("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
            {
                data.Disks.Add(new DiskItem
                {
                    Drive = Text(o, "DeviceID") ?? "",
                    Label = Text(o, "VolumeName"),
                    FileSystem = Text(o, "FileSystem"),
                    SizeBytes = Number(o, "Size"),
                    FreeBytes = Number(o, "FreeSpace"),
                });
            }
        }

        private static void ReadNetwork(InventoryData data)
        {
            foreach (var o in Query("SELECT Description, MACAddress, IPAddress, DefaultIPGateway, DNSServerSearchOrder, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
            {
                data.NetworkAdapters.Add(new NetworkAdapterItem
                {
                    Name = Text(o, "Description") ?? "",
                    MacAddress = Text(o, "MACAddress"),
                    IpAddresses = List(o, "IPAddress"),
                    Gateways = List(o, "DefaultIPGateway"),
                    DnsServers = List(o, "DNSServerSearchOrder"),
                    DhcpEnabled = o["DHCPEnabled"] is bool dhcp && dhcp,
                });
            }
        }

        private static void ReadServices(InventoryData data)
        {
            foreach (var o in Query("SELECT Name, DisplayName, State, StartMode, StartName FROM Win32_Service"))
            {
                data.Services.Add(new ServiceItem
                {
                    Name = Text(o, "Name") ?? "",
                    DisplayName = Text(o, "DisplayName"),
                    State = Text(o, "State"),
                    StartMode = Text(o, "StartMode"),
                    Account = Text(o, "StartName"),
                });
            }
            data.Services = data.Services.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ReadLocalUsers(InventoryData data)
        {
            // LocalAccount = TRUE hält die Abfrage lokal; ohne den Filter würde WMI auf Domänenrechnern die ganze Domäne aufzählen.
            foreach (var o in Query("SELECT Name, FullName, Description, Disabled, SID FROM Win32_UserAccount WHERE LocalAccount = TRUE"))
            {
                data.LocalUsers.Add(new LocalUserItem
                {
                    Name = Text(o, "Name") ?? "",
                    FullName = Text(o, "FullName"),
                    Description = Text(o, "Description"),
                    Disabled = o["Disabled"] is bool disabled && disabled,
                    Sid = Text(o, "SID"),
                });
            }
            data.LocalUsers = data.LocalUsers.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ReadLocalAdmins(InventoryData data)
        {
            // Die Gruppe heißt je nach Windows-Sprache anders (Administrators/Administratoren); die SID ist überall gleich.
            var account = (NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount));
            var groupName = account.Value.Substring(account.Value.IndexOf('\\') + 1);

            var resume = IntPtr.Zero;
            var status = NetLocalGroupGetMembers(null, groupName, 2, out var buffer, -1, out var read, out _, ref resume);
            if (status != 0)
                throw new System.ComponentModel.Win32Exception(status);
            try
            {
                var size = Marshal.SizeOf(typeof(LOCALGROUP_MEMBERS_INFO_2));
                for (var i = 0; i < read; i++)
                {
                    var member = (LOCALGROUP_MEMBERS_INFO_2)Marshal.PtrToStructure(buffer + i * size, typeof(LOCALGROUP_MEMBERS_INFO_2));
                    var sid = new SecurityIdentifier(member.Sid).Value;
                    data.LocalAdmins.Add(new LocalAdminItem
                    {
                        // Verwaiste Einträge (gelöschte Domänenkonten) haben keinen Namen mehr.
                        Name = string.IsNullOrEmpty(member.DomainAndName) ? sid : member.DomainAndName,
                        IsGroup = member.SidUsage == SidTypeGroup || member.SidUsage == SidTypeAlias || member.SidUsage == SidTypeWellKnownGroup,
                        Sid = sid,
                    });
                }
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
            data.LocalAdmins = data.LocalAdmins.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private const int SidTypeGroup = 2;
        private const int SidTypeAlias = 4;
        private const int SidTypeWellKnownGroup = 5;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOCALGROUP_MEMBERS_INFO_2
        {
            public IntPtr Sid;
            public int SidUsage;
            public string DomainAndName;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr buffer,
            int preferredMaxLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        private static List<ManagementBaseObject> Query(string wql)
        {
            using (var searcher = new ManagementObjectSearcher(@"root\cimv2", wql))
            using (var results = searcher.Get())
                return results.Cast<ManagementBaseObject>().ToList();
        }

        private static string? Text(ManagementBaseObject o, string property)
        {
            var value = o[property]?.ToString()?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static long Number(ManagementBaseObject o, string property) =>
            o[property] == null ? 0 : Convert.ToInt64(o[property]);

        private static DateTime? Date(ManagementBaseObject o, string property) =>
            o[property] is string dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime() : (DateTime?)null;

        private static List<string> List(ManagementBaseObject o, string property) =>
            o[property] is string[] values ? values.ToList() : new List<string>();
    }
}
