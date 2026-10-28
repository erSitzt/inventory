using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    /// <summary>
    /// Liest, was die Lizenzberechnung braucht. Nur lesend; Namensräume, die es auf dem Rechner nicht gibt (Hyper-V, Failover-Cluster,
    /// Serverrollen auf Arbeitsplätzen), sind kein Fehler und bleiben null.
    /// </summary>
    internal static class LicensingCollector
    {
        /// <summary>Kennzeichnet Windows selbst unter den Lizenzprodukten (nicht Office).</summary>
        private const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";

        public static LicensingInfo Read(List<string> warnings)
        {
            var info = new LicensingInfo();
            Step(warnings, "Lizenz: Prozessoren", () =>
            {
                var cpus = Query(@"root\cimv2", "SELECT NumberOfCores FROM Win32_Processor");
                info.CpuSockets = cpus.Count;
                info.CpuCoresPerSocket = cpus.Count == 0 ? (int?)null : cpus.Max(o => (int)Number(o, "NumberOfCores"));
            });
            Step(warnings, "Lizenz: BIOS-UUID", () =>
            {
                foreach (var o in Query(@"root\cimv2", "SELECT UUID FROM Win32_ComputerSystemProduct"))
                    info.BiosUuid = Text(o, "UUID");
            });
            // HypervisorPresent gibt es erst ab Windows 8 / Server 2012: auf älteren Systemen still null.
            Optional(() =>
            {
                foreach (var o in Query(@"root\cimv2", "SELECT HypervisorPresent FROM Win32_ComputerSystem"))
                    info.HypervisorPresent = o["HypervisorPresent"] as bool?;
            });
            Step(warnings, "Lizenz: Gastdienste", () => ReadGuestParameters(info));
            Step(warnings, "Lizenz: Edition", () => ReadEdition(info));
            Step(warnings, "Lizenz: Lizenzkanal", () => info.LicenseChannel = ReadLicenseChannel());
            // Serverrollen gibt es nur auf Server-Betriebssystemen.
            if (info.OsProductType is 2 or 3)
                Step(warnings, "Lizenz: Serverrollen", () => info.ServerFeatures = Query(@"root\cimv2", "SELECT ID, Name FROM Win32_ServerFeature")
                    .Select(o => new ServerFeatureItem { Id = (int)Number(o, "ID"), Name = Text(o, "Name") ?? "" }).ToList());
            Step(warnings, "Lizenz: SQL Server", () => info.SqlInstances = ReadSqlInstances());
            Optional(() =>
            {
                foreach (var o in Query(@"root\MSCluster", "SELECT Name FROM MSCluster_Cluster"))
                    info.ClusterName = Text(o, "Name");
            });
            Optional(() => info.VirtualMachines = ReadHyperVMachines());
            return info;
        }

        private static void ReadGuestParameters(LicensingInfo info)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters"))
            {
                info.HyperVGuest = key != null;
                if (key == null)
                    return;
                var host = key.GetValue("PhysicalHostNameFullyQualified") as string;
                info.ReportedHostName = string.IsNullOrWhiteSpace(host) ? key.GetValue("PhysicalHostName") as string : host;
            }
        }

        private static void ReadEdition(LicensingInfo info)
        {
            foreach (var o in Query(@"root\cimv2", "SELECT OperatingSystemSKU, ProductType FROM Win32_OperatingSystem"))
            {
                info.OsSku = o["OperatingSystemSKU"] == null ? (int?)null : (int)Number(o, "OperatingSystemSKU");
                info.OsProductType = o["ProductType"] == null ? (int?)null : (int)Number(o, "ProductType");
            }
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                info.OsEdition = key?.GetValue("EditionID") as string;
        }

        /// <summary>Der Kanal der aktiven Windows-Lizenz; ProductKeyChannel gibt es erst ab Windows 8, davor zählt die Beschreibung.</summary>
        private static string? ReadLicenseChannel()
        {
            var where = $"WHERE ApplicationID = '{WindowsApplicationId}' AND PartialProductKey IS NOT NULL";
            List<ManagementBaseObject> products;
            var withChannel = true;
            try
            {
                products = Query(@"root\cimv2", "SELECT ProductKeyChannel, Description, LicenseStatus FROM SoftwareLicensingProduct " + where);
            }
            catch (ManagementException)
            {
                withChannel = false;
                products = Query(@"root\cimv2", "SELECT Description, LicenseStatus FROM SoftwareLicensingProduct " + where);
            }
            // Bevorzugt die aktivierte Lizenz; sonst die erste mit Schlüssel.
            var product = products.FirstOrDefault(o => Number(o, "LicenseStatus") == 1) ?? products.FirstOrDefault();
            if (product == null)
                return null;
            return LicenseChannels.Normalize(withChannel ? Text(product, "ProductKeyChannel") : null, Text(product, "Description"));
        }

        /// <summary>Instanzen aus der Registry, 64- und 32-Bit-Installationen; eine leere Liste heißt: kein SQL Server.</summary>
        private static List<SqlInstanceItem> ReadSqlInstances()
        {
            var result = new List<SqlInstanceItem>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var names = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
                {
                    if (names == null)
                        continue;
                    foreach (var name in names.GetValueNames())
                    {
                        var id = names.GetValue(name) as string;
                        if (result.Any(r => string.Equals(r.InstanceId, id, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var item = new SqlInstanceItem { Name = name, InstanceId = id };
                        if (id != null)
                        {
                            using (var setup = hklm.OpenSubKey($@"SOFTWARE\Microsoft\Microsoft SQL Server\{id}\Setup"))
                            {
                                item.Edition = setup?.GetValue("Edition") as string;
                                item.Version = setup?.GetValue("Version") as string;
                                item.PatchLevel = setup?.GetValue("PatchLevel") as string;
                            }
                            using (var cluster = hklm.OpenSubKey($@"SOFTWARE\Microsoft\Microsoft SQL Server\{id}\Cluster"))
                                item.Clustered = cluster != null;
                        }
                        result.Add(item);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// VMs eines Hyper-V-Hosts über root\virtualization\v2 (ab Server 2012). Der Host selbst steht ebenfalls in Msvm_ComputerSystem;
        /// VMs erkennt man an der GUID als Name, nicht an der übersetzten Beschriftung.
        /// </summary>
        private static List<HostedVmItem> ReadHyperVMachines()
        {
            const string scope = @"root\virtualization\v2";
            var settings = Query(scope, "SELECT VirtualSystemIdentifier, BIOSGUID FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'")
                .Where(o => Text(o, "VirtualSystemIdentifier") != null)
                .GroupBy(o => Text(o, "VirtualSystemIdentifier")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Text(g.First(), "BIOSGUID"), StringComparer.OrdinalIgnoreCase);
            var processors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in Query(scope, "SELECT InstanceID, VirtualQuantity FROM Msvm_ProcessorSettingData"))
            {
                // InstanceID: Microsoft:<VM-GUID>\<Gerät>\<Index>; Momentaufnahmen tragen ihre eigene GUID und fallen so heraus.
                var instance = Text(o, "InstanceID");
                var start = instance?.IndexOf(':') ?? -1;
                var end = instance?.IndexOf('\\') ?? -1;
                if (instance == null || start < 0 || end <= start)
                    continue;
                var vm = instance.Substring(start + 1, end - start - 1);
                if (settings.ContainsKey(vm) && !processors.ContainsKey(vm))
                    processors[vm] = (int)Number(o, "VirtualQuantity");
            }
            var result = new List<HostedVmItem>();
            foreach (var o in Query(scope, "SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem"))
            {
                var id = Text(o, "Name");
                if (id == null || !Guid.TryParse(id, out _))
                    continue;
                result.Add(new HostedVmItem
                {
                    Name = Text(o, "ElementName") ?? id,
                    BiosUuid = settings.TryGetValue(id, out var bios) ? bios : null,
                    VirtualCpus = processors.TryGetValue(id, out var cpus) ? cpus : (int?)null,
                    // 2 = eingeschaltet
                    Running = Number(o, "EnabledState") == 2,
                });
            }
            return result;
        }

        private static void Step(List<string> warnings, string name, Action read)
        {
            try
            {
                read();
            }
            catch (Exception ex)
            {
                warnings.Add($"{name}: {ex.Message}");
            }
        }

        /// <summary>Für Namensräume und Eigenschaften, die nicht jedes System hat: ein Fehler heißt "nicht vorhanden".</summary>
        private static void Optional(Action read)
        {
            try
            {
                read();
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
            }
        }

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
    }
}
