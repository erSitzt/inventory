using System;
using System.Diagnostics;
using System.IO;
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
                return $"Windows-Version {os} ist älter als {conditions.MinOsVersion}";
            if (!string.IsNullOrWhiteSpace(conditions.BelowOsVersion) && VersionComparer.Compare(os, conditions.BelowOsVersion) >= 0)
                return $"Windows-Version {os} ist nicht älter als {conditions.BelowOsVersion}";

            if (!string.IsNullOrWhiteSpace(conditions.Architecture))
            {
                var actual = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                if (!string.Equals(actual, conditions.Architecture!.Trim(), StringComparison.OrdinalIgnoreCase))
                    return $"Architektur ist {actual}, nicht {conditions.Architecture}";
            }

            if (!string.IsNullOrWhiteSpace(conditions.FileExists) && !PathExists(conditions.FileExists!))
                return $"{conditions.FileExists} ist nicht vorhanden";
            if (!string.IsNullOrWhiteSpace(conditions.FileMissing) && PathExists(conditions.FileMissing!))
                return $"{conditions.FileMissing} ist vorhanden";

            if (!string.IsNullOrWhiteSpace(conditions.RegistryKeyExists) && !RegistryKeyExists(conditions.RegistryKeyExists!))
                return $"Registry-Schlüssel {conditions.RegistryKeyExists} ist nicht vorhanden";
            if (!string.IsNullOrWhiteSpace(conditions.RegistryKeyMissing) && RegistryKeyExists(conditions.RegistryKeyMissing!))
                return $"Registry-Schlüssel {conditions.RegistryKeyMissing} ist vorhanden";

            if (conditions.UserLoggedOn is bool expected)
            {
                // Eine laufende Shell erfasst auch RDP-Sitzungen, nicht nur die Konsole.
                var loggedOn = Process.GetProcessesByName("explorer").Length > 0;
                if (loggedOn != expected)
                    return loggedOn ? "Ein Benutzer ist angemeldet" : "Kein Benutzer angemeldet";
            }
            return null;
        }

        private static bool PathExists(string path)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return File.Exists(expanded) || Directory.Exists(expanded);
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
