using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Reflection;
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
            data.Security = new SecurityStatus();
            Section(data, "Firewall", () => ReadFirewall(data.Security));
            Section(data, "Defender", () => ReadDefender(data.Security));
            Section(data, "Virenschutz", () => ReadAntivirusProducts(data.Security));
            Section(data, "Windows Update", () => ReadWindowsUpdate(data.Security));
            // Nach den lokalen Benutzern: das vordefinierte Administratorkonto wird aus deren Liste gelesen.
            data.Security.Posture = ReadPosture(data);
            data.RemoteAccess = new RemoteAccessInfo();
            Section(data, "Fernzugriff", () => ReadRemoteAccess(data.RemoteAccess));
            var hardware = data.Hardware = new HardwareInfo();
            Section(data, "Gehäuse und Firmware", HardwareAreas.Firmware, () => ReadFirmware(hardware));
            Section(data, "TPM", HardwareAreas.Tpm, () => ReadTpm(hardware));
            Section(data, "Grafik", HardwareAreas.Graphics, () => ReadGraphics(hardware));
            Section(data, "Speichermodule", HardwareAreas.Memory, () => ReadMemoryModules(hardware));
            Section(data, "Physische Datenträger", HardwareAreas.Disks, () => ReadPhysicalDisks(hardware));
            Section(data, "BitLocker", HardwareAreas.Volumes, () => ReadVolumeEncryption(hardware));
            Section(data, "Akku", HardwareAreas.Battery, () => ReadBattery(hardware));
            Section(data, "Monitore", HardwareAreas.Monitors, () => ReadMonitors(hardware));
            Section(data, "Drucker", HardwareAreas.Printers, () => ReadPrinters(hardware));
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

        /// <summary>Wie <see cref="Section(InventoryData, string, Action)"/>, merkt aber zusätzlich den Bereich als nicht gelesen vor.</summary>
        private static void Section(InventoryData data, string name, string area, Action read)
        {
            try
            {
                read();
            }
            catch (Exception ex)
            {
                data.Warnings.Add($"{name}: {ex.Message}");
                data.Hardware?.Failed.Add(area);
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

        /// <summary>Nur die Programmliste, z. B. für Bedingungen und die Erkennung installierter Pakete.</summary>
        internal static List<SoftwareItem> InstalledSoftware()
        {
            var data = new InventoryData();
            ReadSoftware(data);
            return data.Software;
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
                var loaded = users.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !n.EndsWith("_Classes", StringComparison.Ordinal)).ToList();
                foreach (var sid in loaded)
                    ReadUninstall(data, users.OpenSubKey(sid + "\\" + UninstallKey), is64Bit: Environment.Is64BitOperatingSystem, perUser: true, userSid: sid);

                // Welche Profile es gibt und welche davon lesbar waren: damit kann der Server Programme abgemeldeter Benutzer behalten.
                data.UserProfiles = new List<UserProfileState>();
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var profiles = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                {
                    var known = profiles?.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal)) ?? Enumerable.Empty<string>();
                    foreach (var sid in known.Union(loaded, StringComparer.OrdinalIgnoreCase))
                        data.UserProfiles.Add(new UserProfileState { Sid = sid, Loaded = loaded.Contains(sid, StringComparer.OrdinalIgnoreCase) });
                }
            }

            data.Software = data.Software
                .GroupBy(s => (s.Name, s.Version, s.Is64Bit, s.PerUser))
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void ReadUninstall(InventoryData data, RegistryKey? uninstall, bool is64Bit, bool perUser, string? userSid = null)
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
                            UserSid = userSid,
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
                var user = new LocalUserItem
                {
                    Name = Text(o, "Name") ?? "",
                    FullName = Text(o, "FullName"),
                    Description = Text(o, "Description"),
                    Disabled = o["Disabled"] is bool disabled && disabled,
                    Sid = Text(o, "SID"),
                };
                ReadAccountTimes(user);
                data.LocalUsers.Add(user);
            }
            data.LocalUsers = data.LocalUsers.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Kennwortalter und letzte Anmeldung stehen nicht in WMI, nur in der Kontenverwaltung.</summary>
        private static void ReadAccountTimes(LocalUserItem user)
        {
            if (user.Name.Length == 0 || NetUserGetInfo(null, user.Name, 2, out var buffer) != 0)
                return;
            try
            {
                var info = (USER_INFO_2)Marshal.PtrToStructure(buffer, typeof(USER_INFO_2));
                user.PasswordAgeDays = (int)(info.PasswordAge / 86400);
                // Sekunden seit 1970; 0 = noch nie angemeldet.
                if (info.LastLogon > 0)
                    user.LastLogonUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(info.LastLogon);
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
        }

        /// <summary>Jeder Wert für sich: was sich nicht lesen lässt, bleibt null und erzeugt eine Warnung, der Rest gilt trotzdem.</summary>
        private static SecurityPosture ReadPosture(InventoryData data)
        {
            var posture = new SecurityPosture();
            Section(data, "Remotedesktop", () =>
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    // Eine Gruppenrichtlinie hat Vorrang vor der lokalen Einstellung.
                    const string policy = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
                    var deny = Dword(hklm, policy, "fDenyTSConnections") ?? Dword(hklm, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
                    posture.RdpEnabled = deny == null ? (bool?)null : deny == 0;
                    var nla = Dword(hklm, policy, "UserAuthentication")
                              ?? Dword(hklm, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication");
                    posture.RdpNla = nla == null ? (bool?)null : nla != 0;
                }
            });
            Section(data, "Benutzerkontensteuerung", () =>
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    const string system = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
                    var lua = Dword(hklm, system, "EnableLUA");
                    if (lua == null)
                        return;
                    posture.UacEnabled = lua != 0;
                    posture.UacLevel = lua == 0 ? 0 : UacLevel(Dword(hklm, system, "ConsentPromptBehaviorAdmin") ?? 5, (Dword(hklm, system, "PromptOnSecureDesktop") ?? 1) != 0);
                }
            });
            Section(data, "SMB 1", () =>
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    const string services = @"SYSTEM\CurrentControlSet\Services\";
                    // Server: der ausdrückliche Schalter gilt; ohne ihn entscheidet, ob der SMB-1-Treiber installiert und nicht deaktiviert ist.
                    var server = Dword(hklm, services + @"LanmanServer\Parameters", "SMB1") is int explicitValue
                        ? explicitValue != 0
                        : DriverActive(hklm, services + "srv");
                    posture.Smb1Enabled = server || DriverActive(hklm, services + "mrxsmb10");
                }
            });
            Section(data, "Virtualisierungsbasierte Sicherheit", () =>
            {
                try
                {
                    foreach (var o in Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard"))
                    {
                        // SecurityServicesRunning: 1 = Credential Guard, 2 = Speicherintegrität (HVCI).
                        var running = o["SecurityServicesRunning"] is Array values ? values.Cast<object>().Select(Convert.ToInt32).ToList() : new List<int>();
                        posture.CredentialGuard = running.Contains(1);
                        posture.MemoryIntegrity = running.Contains(2);
                    }
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
                {
                    // Ohne die Klasse (ältere Server) gibt es beides nicht.
                    posture.CredentialGuard = false;
                    posture.MemoryIntegrity = false;
                }
            });
            Section(data, "LSA-Schutz", () =>
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    const string lsa = @"SYSTEM\CurrentControlSet\Control\Lsa";
                    // RunAsPPL: 1 = mit UEFI-Sperre, 2 = ohne; Windows 11 schaltet den Schutz bei Neuinstallationen selbst über RunAsPPLBoot ein.
                    posture.LsaProtection = (Dword(hklm, lsa, "RunAsPPL") ?? 0) != 0 || (Dword(hklm, lsa, "RunAsPPLBoot") ?? 0) != 0;
                }
            });
            // Das vordefinierte Konto heißt je nach Sprache und Umbenennung anders; seine SID endet immer auf -500.
            var builtin = data.LocalUsers.FirstOrDefault(u => u.Sid != null && u.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && u.Sid.EndsWith("-500", StringComparison.Ordinal));
            if (builtin != null)
            {
                posture.BuiltinAdminEnabled = !builtin.Disabled;
                posture.BuiltinAdminPasswordAgeDays = builtin.PasswordAgeDays;
            }
            Section(data, "Aktivierung", () =>
            {
                // Die ApplicationID kennzeichnet Windows selbst (nicht Office); nur Einträge mit eingetragenem Schlüssel zählen.
                foreach (var o in Query("SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL"))
                    posture.WindowsActivated = posture.WindowsActivated == true || Number(o, "LicenseStatus") == 1;
            });
            return posture;
        }

        /// <summary>Übersetzt die beiden Registry-Werte in die vier Stufen des Schiebereglers.</summary>
        private static int UacLevel(int consentPrompt, bool secureDesktop)
        {
            switch (consentPrompt)
            {
                case 0: return 0;
                // 1/3 verlangen Anmeldedaten, 2 die Bestätigung bei jeder Änderung: alles strenger als der Standard.
                case 1:
                case 2:
                case 3: return 3;
                default: return secureDesktop ? 2 : 1;
            }
        }

        private static int? Dword(RegistryKey hive, string path, string name)
        {
            using (var key = hive.OpenSubKey(path))
                return key?.GetValue(name) as int?;
        }

        /// <summary>Start = 4 bedeutet "deaktiviert"; fehlt der Schlüssel, ist der Treiber nicht installiert.</summary>
        private static bool DriverActive(RegistryKey hive, string path)
        {
            using (var key = hive.OpenSubKey(path))
                return key != null && (key.GetValue("Start") as int? ?? 4) != 4;
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

        private static void ReadRemoteAccess(RemoteAccessInfo info)
        {
            info.SessionId = UserSession.ActiveSessionId();

            // TeamViewer legt seine Geräte-ID je nach Version unter dem 64- oder 32-Bit-Zweig ab.
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\TeamViewer"))
                {
                    var id = key?.GetValue("ClientID");
                    if (id != null && info.TeamViewerId == null && Convert.ToInt64(id) > 0)
                        info.TeamViewerId = Convert.ToInt64(id).ToString();
                }
            }

            // AnyDesk speichert die ID in einer Textdatei; installiert unter ProgramData.
            var anyDesk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AnyDesk", "system.conf");
            if (System.IO.File.Exists(anyDesk))
            {
                foreach (var line in System.IO.File.ReadAllLines(anyDesk))
                {
                    if (line.StartsWith("ad.anynet.id=", StringComparison.Ordinal))
                        info.AnyDeskId = line.Substring("ad.anynet.id=".Length).Trim();
                }
            }
        }

        private static void ReadFirmware(HardwareInfo hardware)
        {
            foreach (var o in Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                if (o["ChassisTypes"] is ushort[] types && types.Length > 0 && hardware.ChassisType == null)
                    hardware.ChassisType = types[0];
            }
            foreach (var o in Query("SELECT ReleaseDate FROM Win32_BIOS"))
                hardware.BiosDateUtc = Date(o, "ReleaseDate");

            // FIRMWARE_TYPE: 1 = BIOS, 2 = UEFI.
            if (GetFirmwareType(out var firmware))
                hardware.FirmwareType = firmware == 2 ? "UEFI" : firmware == 1 ? "BIOS" : null;

            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var state = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                {
                    if (state?.GetValue("UEFISecureBootEnabled") is int enabled)
                        hardware.SecureBoot = enabled != 0;
                    else if (hardware.FirmwareType == "BIOS")
                        hardware.SecureBoot = false;
                }
            }
        }

        private static void ReadTpm(HardwareInfo hardware)
        {
            List<ManagementBaseObject> rows;
            try
            {
                rows = Query(@"root\cimv2\Security\MicrosoftTpm", "SELECT * FROM Win32_Tpm");
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                // Ohne den TPM-Anbieter (manche Server-Installationen) lässt sich nichts sagen.
                return;
            }
            hardware.TpmPresent = rows.Count > 0;
            foreach (var o in rows)
            {
                // SpecVersion: "2.0, 0, 1.59" – vorn steht die Version der Spezifikation.
                hardware.TpmVersion = Text(o, "SpecVersion")?.Split(',')[0].Trim();
                hardware.TpmReady = Flag(o, "IsEnabled_InitialValue") == true && Flag(o, "IsActivated_InitialValue") == true && Flag(o, "IsOwned_InitialValue") == true;
            }
        }

        private static void ReadGraphics(HardwareInfo hardware)
        {
            foreach (var o in Query("SELECT Name, DriverVersion FROM Win32_VideoController"))
            {
                var name = Text(o, "Name");
                // Fernwartungsprogramme und RDP-Sitzungen melden eigene Anzeigetreiber; das sind keine Grafikkarten.
                if (name != null && (name.IndexOf("Mirror Driver", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Remote Display", StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                if (name != null && hardware.Gpus.All(g => g.Name != name))
                    hardware.Gpus.Add(new GpuItem { Name = name, DriverVersion = Text(o, "DriverVersion") });
            }
        }

        private static void ReadMemoryModules(HardwareInfo hardware)
        {
            var slots = 0;
            foreach (var o in Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                slots += (int)Number(o, "MemoryDevices");
            hardware.MemorySlots = slots > 0 ? slots : (int?)null;

            foreach (var o in Query("SELECT DeviceLocator, Manufacturer, PartNumber, SerialNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                // Speed ist die Nenngeschwindigkeit des Moduls, ConfiguredClockSpeed die tatsächlich eingestellte.
                var speed = (int)Number(o, "ConfiguredClockSpeed");
                if (speed == 0)
                    speed = (int)Number(o, "Speed");
                hardware.MemoryModules.Add(new MemoryModuleItem
                {
                    Slot = Text(o, "DeviceLocator") ?? "",
                    Manufacturer = Text(o, "Manufacturer"),
                    PartNumber = Text(o, "PartNumber"),
                    SerialNumber = Text(o, "SerialNumber"),
                    CapacityBytes = Number(o, "Capacity"),
                    SpeedMhz = speed > 0 ? speed : (int?)null,
                    Type = MemoryType((int)Number(o, "SMBIOSMemoryType")),
                });
            }
        }

        private static string? MemoryType(int smbiosType)
        {
            switch (smbiosType)
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                default: return null;
            }
        }

        private static void ReadPhysicalDisks(HardwareInfo hardware)
        {
            try
            {
                foreach (var o in Query(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, SerialNumber, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"))
                {
                    var media = (int)Number(o, "MediaType");
                    var health = o["HealthStatus"] == null ? -1 : Convert.ToInt32(o["HealthStatus"]);
                    hardware.PhysicalDisks.Add(new PhysicalDiskItem
                    {
                        Model = Text(o, "FriendlyName") ?? "",
                        SerialNumber = Text(o, "SerialNumber"),
                        MediaType = media == 4 ? "SSD" : media == 3 ? "HDD" : null,
                        BusType = BusType((int)Number(o, "BusType")),
                        SizeBytes = Number(o, "Size"),
                        Health = health == 0 ? "Healthy" : health == 1 ? "Warning" : health == 2 ? "Unhealthy" : null,
                    });
                }
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                // Ohne die Speicherverwaltungs-API bleibt die klassische Klasse: kein SSD/HDD, kein Zustand.
                foreach (var o in Query("SELECT Model, SerialNumber, Size, InterfaceType FROM Win32_DiskDrive"))
                {
                    hardware.PhysicalDisks.Add(new PhysicalDiskItem
                    {
                        Model = Text(o, "Model") ?? "",
                        SerialNumber = Text(o, "SerialNumber"),
                        BusType = Text(o, "InterfaceType"),
                        SizeBytes = Number(o, "Size"),
                    });
                }
            }
        }

        private static string? BusType(int busType)
        {
            switch (busType)
            {
                case 1: return "SCSI";
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14:
                case 15: return "Virtual";
                case 16: return "Storage Spaces";
                case 17: return "NVMe";
                default: return null;
            }
        }

        private static void ReadVolumeEncryption(HardwareInfo hardware)
        {
            List<ManagementBaseObject> rows;
            try
            {
                rows = Query(@"root\cimv2\Security\MicrosoftVolumeEncryption", "SELECT * FROM Win32_EncryptableVolume");
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                // BitLocker ist nicht installiert (Server ohne das Feature, Home-Editionen ohne Geräteverschlüsselung).
                return;
            }
            foreach (var o in rows)
            {
                var drive = Text(o, "DriveLetter");
                if (drive == null)
                    continue;
                // ProtectionStatus: 0 = aus, 1 = an, 2 = unbekannt (Laufwerk gesperrt).
                var protection = o["ProtectionStatus"] == null ? 2 : Convert.ToInt32(o["ProtectionStatus"]);
                var item = new VolumeEncryptionItem { Drive = drive, Protected = protection == 2 ? (bool?)null : protection == 1 };
                try
                {
                    if (o is ManagementObject volume)
                    {
                        using (var status = volume.InvokeMethod("GetConversionStatus", null, null))
                        {
                            if (status?["EncryptionPercentage"] != null)
                                item.EncryptedPercent = Convert.ToInt32(status["EncryptionPercentage"]);
                        }
                        using (var method = volume.InvokeMethod("GetEncryptionMethod", null, null))
                        {
                            if (method?["EncryptionMethod"] != null)
                                item.Method = EncryptionMethod(Convert.ToInt32(method["EncryptionMethod"]));
                        }
                    }
                }
                catch (Exception ex) when (ex is ManagementException || ex is COMException)
                {
                    // Der Schutzstatus allein ist schon eine Aussage.
                }
                hardware.Volumes.Add(item);
            }
            hardware.Volumes = hardware.Volumes.OrderBy(v => v.Drive, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string? EncryptionMethod(int method)
        {
            switch (method)
            {
                case 1: return "AES 128 (Diffuser)";
                case 2: return "AES 256 (Diffuser)";
                case 3: return "AES 128";
                case 4: return "AES 256";
                case 5: return "Hardware";
                case 6: return "XTS-AES 128";
                case 7: return "XTS-AES 256";
                default: return null;
            }
        }

        private static void ReadBattery(HardwareInfo hardware)
        {
            hardware.BatteryPresent = Query("SELECT DeviceID FROM Win32_Battery").Count > 0;
            if (hardware.BatteryPresent != true)
                return;
            try
            {
                long designed = 0, full = 0;
                foreach (var o in Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData"))
                    designed += Number(o, "DesignedCapacity");
                foreach (var o in Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                    full += Number(o, "FullChargedCapacity");
                if (designed > 0 && full > 0)
                    hardware.BatteryHealthPercent = (int)Math.Min(100, Math.Round(100d * full / designed));
                foreach (var o in Query(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount"))
                {
                    var cycles = (int)Number(o, "CycleCount");
                    if (cycles > 0)
                        hardware.BatteryCycleCount = Math.Max(hardware.BatteryCycleCount ?? 0, cycles);
                }
            }
            catch (ManagementException)
            {
                // Nicht jeder Akkutreiber liefert die Kapazitäten; dass ein Akku da ist, bleibt bekannt.
            }
        }

        private static void ReadMonitors(HardwareInfo hardware)
        {
            List<ManagementBaseObject> rows;
            try
            {
                rows = Query(@"root\wmi", "SELECT ManufacturerName, UserFriendlyName, ProductCodeID, SerialNumberID, YearOfManufacture FROM WmiMonitorID");
            }
            catch (ManagementException)
            {
                // "Nicht unterstützt", wenn kein Monitor mit EDID angeschlossen ist (Server, virtuelle Maschinen).
                return;
            }
            foreach (var o in rows)
            {
                var manufacturer = Chars(o, "ManufacturerName");
                var year = (int)Number(o, "YearOfManufacture");
                var serial = Chars(o, "SerialNumberID");
                hardware.Monitors.Add(new MonitorItem
                {
                    Manufacturer = manufacturer,
                    // Eingebaute Displays haben oft keinen Namen, nur Hersteller- und Produktkennung.
                    Model = Chars(o, "UserFriendlyName") ?? (manufacturer + Chars(o, "ProductCodeID")),
                    SerialNumber = serial == "0" ? null : serial,
                    Year = year > 1990 ? year : (int?)null,
                });
            }
        }

        private static void ReadPrinters(HardwareInfo hardware)
        {
            // Sichtbar sind die für den ganzen Rechner eingerichteten Drucker; Verbindungen einzelner Benutzer nicht.
            foreach (var o in Query("SELECT Name, DriverName, PortName, Network, Shared FROM Win32_Printer"))
            {
                var name = Text(o, "Name");
                var port = Text(o, "PortName");
                if (name == null || IsVirtualPrinter(name, port))
                    continue;
                hardware.Printers.Add(new PrinterItem
                {
                    Name = name,
                    Driver = Text(o, "DriverName"),
                    Port = port,
                    Network = o["Network"] is bool network && network,
                    Shared = o["Shared"] is bool shared && shared,
                });
            }
            hardware.Printers = hardware.Printers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Microsoft Print to PDF", XPS, Fax und OneNote hat jeder Rechner; sie sagen nichts aus.</summary>
        private static bool IsVirtualPrinter(string name, string? port)
        {
            foreach (var virtualPort in new[] { "PORTPROMPT:", "SHRFAX:", "XPSPort:", "nul:" })
            {
                if (string.Equals(port, virtualPort, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return name.StartsWith("OneNote", StringComparison.OrdinalIgnoreCase)
                   || (port != null && port.StartsWith("Microsoft.Office.OneNote", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>WMI liefert EDID-Texte als Zeichencodes mit Nullen am Ende.</summary>
        private static string? Chars(ManagementBaseObject o, string property)
        {
            if (!(o[property] is ushort[] codes))
                return null;
            var text = new string(codes.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
            return text.Length == 0 ? null : text;
        }

        private static void ReadFirewall(SecurityStatus status)
        {
            // Maßgeblich ist der "ActiveStore" (MSFT_NetFirewallProfile mit PolicyStore=ActiveStore): der wirksame Zustand
            // einschließlich aller Richtlinien, den auch Windows-Sicherheit und Get-NetFirewallProfile -PolicyStore ActiveStore
            // zeigen. Firewall-API und Registry sehen Richtlinien aus MDM/Intune nicht und sind nur der Ausweg.
            var active = FirewallActiveStore();
            status.FirewallDomain = Lookup(active, "Domain") ?? FirewallEnabledByApi(1) ?? FirewallEnabled("DomainProfile");
            status.FirewallPrivate = Lookup(active, "Private") ?? FirewallEnabledByApi(2) ?? FirewallEnabled("StandardProfile");
            status.FirewallPublic = Lookup(active, "Public") ?? FirewallEnabledByApi(4) ?? FirewallEnabled("PublicProfile");
        }

        private static bool? Lookup(Dictionary<string, bool?> values, string key) =>
            values.TryGetValue(key, out var value) ? value : null;

        private static Dictionary<string, bool?> FirewallActiveStore()
        {
            var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scope = new ManagementScope(@"\\.\root\StandardCimv2");
                scope.Connect();
                var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(30) };
                options.Context.Add("PolicyStore", "ActiveStore");
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, Enabled FROM MSFT_NetFirewallProfile"), options))
                {
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        var name = Text(o, "Name");
                        // Enabled: 0 = False, 1 = True, 2 = NotConfigured (dann gilt der Standard "an").
                        var enabled = o["Enabled"] == null ? (int?)null : Convert.ToInt32(o["Enabled"]);
                        if (name != null && enabled != null)
                            result[name] = enabled != 0;
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException || ex is COMException || ex is UnauthorizedAccessException)
            {
                // Namespace oder Klasse fehlt (sehr alte Systeme): die Ausweichwege übernehmen.
            }
            return result;
        }

        /// <param name="profileType">NET_FW_PROFILE_TYPE2: 1 = Domäne, 2 = Privat, 4 = Öffentlich.</param>
        private static bool? FirewallEnabledByApi(int profileType)
        {
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null)
                    return null;
                var policy = Activator.CreateInstance(type);
                var enabled = type.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, new object[] { profileType });
                return enabled is bool value ? value : (bool?)null;
            }
            catch (Exception ex) when (ex is COMException || ex is TargetInvocationException || ex is MissingMethodException || ex is InvalidCastException)
            {
                return null;
            }
        }

        private static bool? FirewallEnabled(string profile)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                // Eine Gruppenrichtlinie hat Vorrang vor der lokalen Einstellung.
                foreach (var path in new[]
                         {
                             @"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + profile,
                             @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + profile,
                         })
                {
                    using (var key = hklm.OpenSubKey(path))
                    {
                        if (key?.GetValue("EnableFirewall") is int enabled)
                            return enabled != 0;
                    }
                }
            }
            return null;
        }

        private static void ReadDefender(SecurityStatus status)
        {
            List<ManagementBaseObject> rows;
            try
            {
                rows = Query(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                // Defender ist nicht installiert (z. B. durch anderen Virenschutz ersetzt oder Server ohne das Feature).
                status.DefenderEnabled = false;
                return;
            }
            foreach (var o in rows)
            {
                status.DefenderEnabled = Flag(o, "AntivirusEnabled");
                status.DefenderRealTime = Flag(o, "RealTimeProtectionEnabled");
                status.DefenderTamperProtected = Flag(o, "IsTamperProtected");
                status.DefenderSignatureVersion = Text(o, "AntivirusSignatureVersion");
                status.DefenderSignatureUpdatedUtc = Date(o, "AntivirusSignatureLastUpdated");
                // "Passive Mode": ein anderer Virenschutz ist der aktive Echtzeitschutz; Defender scannt dann nur noch auf Anforderung.
                status.DefenderMode = Text(o, "AMRunningMode");
                status.DefenderLastQuickScanUtc = Date(o, "QuickScanEndTime");
            }
        }

        private static void ReadAntivirusProducts(SecurityStatus status)
        {
            try
            {
                foreach (var o in Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
                {
                    var name = Text(o, "displayName");
                    if (name == null || status.AntivirusProducts.Contains(name))
                        continue;
                    status.AntivirusProducts.Add(name);
                    // productState: Bits 12–15 = 1, wenn der Schutz eingeschaltet ist; Bits 4–7 = 1, wenn die Definitionen veraltet sind.
                    var state = Convert.ToInt32(o["productState"] ?? 0);
                    status.AntivirusProductDetails.Add(new AntivirusProductItem
                    {
                        Name = name,
                        Enabled = ((state >> 12) & 0xF) == 1,
                        UpToDate = ((state >> 4) & 0xF) == 0,
                    });
                }
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
            {
                // Das Sicherheitscenter gibt es auf Server-Betriebssystemen nicht.
            }
        }

        private static void ReadWindowsUpdate(SecurityStatus status)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var policy = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"))
                    status.UpdateServer = policy?.GetValue("WUServer") as string;

                var reboot = false;
                foreach (var path in new[]
                         {
                             @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                             @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\PostRebootReporting",
                             @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
                             @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending",
                         })
                {
                    using (var key = hklm.OpenSubKey(path))
                        reboot |= key != null;
                }
                status.UpdateRebootRequired = reboot;
            }

            // Der Windows-Update-Agent wird über COM angesprochen; ein hängender Dienst darf den Scan nicht aufhalten.
            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    dynamic auto = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", true));
                    status.UpdateLastSearchUtc = ComDate(auto.Results.LastSearchSuccessDate);
                    status.UpdateLastInstallUtc = ComDate(auto.Results.LastInstallationSuccessDate);
                    // Der Update-Agent selbst weiß am verlässlichsten, ob ein Neustart aussteht; die Registry-Schlüssel oben ergänzen das.
                    dynamic systemInfo = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", true));
                    if ((bool)systemInfo.RebootRequired)
                        status.UpdateRebootRequired = true;

                    dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
                    dynamic searcher = session.CreateUpdateSearcher();
                    // Offline: nur auswerten, was der Rechner bei seiner letzten eigenen Suche gefunden hat. Das dauert Sekunden
                    // statt Minuten und erzeugt keine Last auf WSUS oder Microsoft Update.
                    searcher.Online = false;
                    dynamic found = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                    var pending = new List<string>();
                    foreach (dynamic update in found.Updates)
                        pending.Add((string)update.Title);
                    pending.Sort(StringComparer.CurrentCultureIgnoreCase);
                    status.UpdatePending = pending;
                }
                catch (Exception ex)
                {
                    UpdateError = ex.Message;
                }
            })
            { IsBackground = true };
            UpdateError = null;
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(90)))
                throw new TimeoutException("Der Windows-Update-Agent hat nicht rechtzeitig geantwortet");
            if (UpdateError != null)
                throw new InvalidOperationException(UpdateError);
        }

        private static volatile string? UpdateError;

        private static DateTime? ComDate(object value) =>
            // Der Agent liefert 1899-12-30 (OLE-Nullwert), wenn es noch nie eine Suche bzw. Installation gab. Die Zeiten sind UTC.
            value is DateTime date && date.Year > 1990 ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : (DateTime?)null;

        private static bool? Flag(ManagementBaseObject o, string property) => o[property] as bool?;

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

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

        /// <summary>Anfang von USER_INFO_2 bis einschließlich der letzten Anmeldung; die restlichen Felder werden nicht gebraucht.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct USER_INFO_2
        {
            public IntPtr Name;
            public IntPtr Password;
            public uint PasswordAge;
            public uint Priv;
            public IntPtr HomeDir;
            public IntPtr Comment;
            public uint Flags;
            public IntPtr ScriptPath;
            public uint AuthFlags;
            public IntPtr FullName;
            public IntPtr UserComment;
            public IntPtr Parms;
            public IntPtr Workstations;
            public uint LastLogon;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFirmwareType(out int firmwareType);

        private static List<ManagementBaseObject> Query(string wql) => Query(@"root\cimv2", wql);

        private static List<ManagementBaseObject> Query(string scope, string wql)
        {
            using (var searcher = new ManagementObjectSearcher(scope, wql))
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
