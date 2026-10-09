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
            data.RemoteAccess = new RemoteAccessInfo();
            Section(data, "Fernzugriff", () => ReadRemoteAccess(data.RemoteAccess));
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
