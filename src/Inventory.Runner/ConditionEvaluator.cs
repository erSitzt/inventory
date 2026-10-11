using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    internal static class ConditionEvaluator
    {
        /// <summary>Liefert null, wenn alle Bedingungen zutreffen, sonst den Grund für das Überspringen.</summary>
        public static string? WhyNot(StepConditions? conditions)
        {
            if (conditions == null || conditions.IsEmpty)
                return null;

            var os = OsVersion();
            if (!string.IsNullOrWhiteSpace(conditions.MinOsVersion) && VersionComparer.Compare(os, conditions.MinOsVersion) < 0)
                return $"Windows version {os} is older than {conditions.MinOsVersion}";
            if (!string.IsNullOrWhiteSpace(conditions.BelowOsVersion) && VersionComparer.Compare(os, conditions.BelowOsVersion) >= 0)
                return $"Windows version {os} is not older than {conditions.BelowOsVersion}";

            if (!string.IsNullOrWhiteSpace(conditions.Architecture))
            {
                var actual = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                if (!string.Equals(actual, conditions.Architecture!.Trim(), StringComparison.OrdinalIgnoreCase))
                    return $"architecture is {actual}, not {conditions.Architecture}";
            }

            if (!string.IsNullOrWhiteSpace(conditions.FileExists) && !PathExists(conditions.FileExists!))
                return $"{conditions.FileExists} does not exist";
            if (!string.IsNullOrWhiteSpace(conditions.FileMissing) && PathExists(conditions.FileMissing!))
                return $"{conditions.FileMissing} exists";

            if (!string.IsNullOrWhiteSpace(conditions.RegistryKeyExists) && !RegistryKeyExists(conditions.RegistryKeyExists!))
                return $"registry key {conditions.RegistryKeyExists} does not exist";
            if (!string.IsNullOrWhiteSpace(conditions.RegistryKeyMissing) && RegistryKeyExists(conditions.RegistryKeyMissing!))
                return $"registry key {conditions.RegistryKeyMissing} exists";

            if (!string.IsNullOrWhiteSpace(conditions.SoftwareInstalled) || !string.IsNullOrWhiteSpace(conditions.SoftwareMissing))
            {
                var software = InventoryCollector.InstalledSoftware();
                if (!string.IsNullOrWhiteSpace(conditions.SoftwareInstalled)
                    && !software.Any(s => SoftwareMatcher.Matches(conditions.SoftwareInstalled, conditions.SoftwareMinVersion, s.Name, s.Version)))
                {
                    // Für die Meldung unterscheiden: gar nicht da oder nur zu alt.
                    var older = software.FirstOrDefault(s => SoftwareMatcher.Matches(conditions.SoftwareInstalled, null, s.Name, s.Version));
                    return older != null
                        ? $"{older.Name} {older.Version} is older than {conditions.SoftwareMinVersion}"
                        : $"{conditions.SoftwareInstalled} is not installed";
                }
                if (!string.IsNullOrWhiteSpace(conditions.SoftwareMissing)
                    && software.FirstOrDefault(s => SoftwareMatcher.Matches(conditions.SoftwareMissing, null, s.Name, s.Version)) is SoftwareItem present)
                    return $"{present.Name} is installed";
            }

            if (!string.IsNullOrWhiteSpace(conditions.MsiProductCode) && !ProductInstalled(conditions.MsiProductCode!))
                return $"MSI product {conditions.MsiProductCode} is not installed";

            if (conditions.UserLoggedOn is bool expected)
            {
                // Eine laufende Shell erfasst auch RDP-Sitzungen, nicht nur die Konsole.
                var loggedOn = Process.GetProcessesByName("explorer").Length > 0;
                if (loggedOn != expected)
                    return loggedOn ? "a user is logged on" : "no user is logged on";
            }
            return null;
        }

        private static bool PathExists(string path)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return File.Exists(expanded) || Directory.Exists(expanded);
        }

        /// <summary>Ein MSI-Produkt trägt sich unter seinem Produktcode in den Uninstall-Schlüssel ein (64- oder 32-Bit-Sicht).</summary>
        private static bool ProductInstalled(string productCode)
        {
            var path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + SoftwareMatcher.NormalizeProductCode(productCode);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = hklm.OpenSubKey(path))
                {
                    if (key != null)
                        return true;
                }
            }
            return false;
        }

        private static string OsVersion()
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                // Environment.OSVersion liefert ohne passendes Manifest eine zu alte Version.
                var major = key?.GetValue("CurrentMajorVersionNumber") as int? ?? Environment.OSVersion.Version.Major;
                var minor = key?.GetValue("CurrentMinorVersionNumber") as int? ?? Environment.OSVersion.Version.Minor;
                var build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
                return $"{major}.{minor}.{build}";
            }
        }

        private static bool RegistryKeyExists(string spec)
        {
            var separator = spec.IndexOf('\\');
            if (separator < 0)
                return false;
            RegistryHive hive;
            switch (spec.Substring(0, separator).ToUpperInvariant())
            {
                case "HKLM": case "HKEY_LOCAL_MACHINE": hive = RegistryHive.LocalMachine; break;
                case "HKCR": case "HKEY_CLASSES_ROOT": hive = RegistryHive.ClassesRoot; break;
                case "HKU": case "HKEY_USERS": hive = RegistryHive.Users; break;
                default: return false;
            }
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var key = root.OpenSubKey(spec.Substring(separator + 1)))
                return key != null;
        }
    }
}
