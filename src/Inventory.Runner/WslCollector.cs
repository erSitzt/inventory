using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Inventory.Contracts;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Inventory.Runner
{
    /// <summary>
    /// Windows-Subsystem für Linux: welche Version eingerichtet ist und welche Distributionen die Benutzer haben. Die
    /// Distributionen stehen in der Registry des jeweiligen Benutzers und sind wie die Software pro Benutzer nur lesbar,
    /// solange sein Profil geladen ist.
    /// </summary>
    internal static class WslCollector
    {
        private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

        /// <summary>Distribution läuft als VM (WSL 2); gesetzt bei Registrierungen ohne den Wert Version.</summary>
        private const int VmModeFlag = 0x8;

        /// <param name="data">Mit gelesenen Apps, Profilen und Features.</param>
        public static WslInfo Read(InventoryData data)
        {
            var wsl = new WslInfo { Version = PackageVersion(data.Apps) };
            var tokens = UserSession.UserTokens();
            try
            {
                ReadDistros(wsl, data.UserProfiles, tokens);
            }
            finally
            {
                foreach (var token in tokens.Values)
                    token.Dispose();
            }
            wsl.Distros = wsl.Distros.OrderBy(d => d.UserName ?? d.UserSid, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // Die Dienste direkt aus der Registry: WMI-Abfragen, die in diesem Scan scheitern, ergäben sonst "kein WSL", und der
            // Server würde die Distributionen abgemeldeter Benutzer verwerfen. LxssManager gibt es mit dem Feature des alten WSL;
            // das Feature selbst zählt zusätzlich, wenn die Liste gelesen wurde.
            var feature = data.OptionalFeatures?.Any(f => string.Equals(f.Name, WslNames.Feature, StringComparison.OrdinalIgnoreCase)) == true;
            wsl.Installed = wsl.Version != null || feature || ServiceExists(WslNames.LegacyService) || ServiceExists(WslNames.PackageService) || wsl.Distros.Count > 0;
            // Ohne die App-Pakete lässt sich das WSL aus dem Store (vor 2.0, ohne eigenen Dienst) nicht ausschließen: dann lieber
            // gar keine Angabe als ein falsches "nicht eingerichtet".
            if (!wsl.Installed && data.Apps == null)
                throw new InvalidOperationException("App-Pakete nicht gelesen, WSL aus dem Store nicht prüfbar");
            return wsl;
        }

        private static void ReadDistros(WslInfo wsl, List<UserProfileState>? profiles, Dictionary<string, SafeAccessTokenHandle> tokens)
        {
            using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            {
                foreach (var sid in users.GetSubKeyNames().Where(UserSids.IsUserHive))
                {
                    using (var lxss = users.OpenSubKey(sid + "\\" + LxssKey))
                    {
                        if (lxss == null)
                            continue;
                        foreach (var id in lxss.GetSubKeyNames())
                        {
                            using (var key = lxss.OpenSubKey(id))
                            {
                                if (key != null && Distro(key, sid, profiles, tokens) is { } distro)
                                    wsl.Distros.Add(distro);
                            }
                        }
                    }
                }
            }
        }

        private static WslDistroItem? Distro(RegistryKey key, string sid, List<UserProfileState>? profiles, Dictionary<string, SafeAccessTokenHandle> tokens)
        {
            if (!(key.GetValue("DistributionName") is string name) || string.IsNullOrWhiteSpace(name))
                return null;
            var distro = new WslDistroItem
            {
                Name = name.Trim(),
                UserSid = sid,
                UserName = profiles?.FirstOrDefault(p => string.Equals(p.Sid, sid, StringComparison.OrdinalIgnoreCase))?.Name,
                WslVersion = key.GetValue("Version") is int version ? version
                    : key.GetValue("Flags") is int flags ? ((flags & VmModeFlag) != 0 ? 2 : 1)
                    : (int?)null,
            };
            if (distro.WslVersion == 2 && WslNames.DiskPath(key.GetValue("BasePath") as string, key.GetValue("VhdFileName") as string) is { } path)
                ReadDiskAs(sid, tokens, () => ReadDisk(distro, path));
            return distro;
        }

        /// <summary>
        /// Den Pfad zur virtuellen Festplatte bestimmt der Benutzer. Gelesen wird darum mit seinem eigenen Token und nie als
        /// Lokales System: Führte der Pfad (auch über einen Link, der erst zwischen Prüfung und Zugriff entsteht) auf einen
        /// fremden Server, meldete sich dort höchstens der Benutzer selbst an, nicht das Computerkonto. Ohne Sitzung des Benutzers
        /// gibt es kein Token und kein Datum; der Server behält dann das bisherige.
        /// </summary>
        private static void ReadDiskAs(string sid, Dictionary<string, SafeAccessTokenHandle> tokens, Action read)
        {
            // Läuft der Runner selbst unter diesem Benutzer (--collect von Hand), gibt es nichts zu leihen.
            using (var current = WindowsIdentity.GetCurrent())
            {
                if (string.Equals(current.User?.Value, sid, StringComparison.OrdinalIgnoreCase))
                {
                    read();
                    return;
                }
            }
            if (tokens.TryGetValue(sid, out var token))
                WindowsIdentity.RunImpersonated(token, read);
        }

        private static bool ServiceExists(string name)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                return key != null;
        }

        /// <summary>
        /// Die virtuelle Festplatte ändert sich, solange die Distribution läuft: ihr Datum ist die letzte Nutzung. Zusätzlich zum
        /// Token des Benutzers nur auf einem lokalen Laufwerk und ohne Verweise im Pfad, siehe <see cref="IsLocal"/>.
        /// </summary>
        private static void ReadDisk(WslDistroItem distro, string path)
        {
            try
            {
                if (!IsLocal(path))
                    return;
                var file = new FileInfo(path);
                if (!file.Exists)
                    return;
                distro.LastUsedUtc = file.LastWriteTimeUtc;
                distro.SizeBytes = file.Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                // Ein Laufwerk, das gerade fehlt, oder ein ungültiger Pfad: dann eben ohne Datum.
            }
        }

        /// <summary>
        /// Lokales Laufwerk, und kein Abschnitt des Pfads ist ein symbolischer Link oder eine Junction (anlegen darf solche Links
        /// im Entwicklermodus jeder Benutzer). Die Attribute eines Links liest Windows, ohne ihm zu folgen.
        /// </summary>
        private static bool IsLocal(string path)
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length != 3)
                return false;
            var type = new DriveInfo(root).DriveType;
            if (type != DriveType.Fixed && type != DriveType.Removable)
                return false;
            var current = root;
            foreach (var part in path.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Seit WSL 2.0 ein MSI-Paket mit wsl.exe unter Programme\WSL; davor ein App-Paket aus dem Store. Das in Windows
        /// enthaltene WSL hat keine eigene Version.
        /// </summary>
        private static string? PackageVersion(List<AppItem>? apps)
        {
            // ProgramW6432: der 64-Bit-Ordner auch dann, wenn der Runner als 32-Bit-Prozess liefe.
            var programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
            if (string.IsNullOrEmpty(programFiles))
                programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var exe = Path.Combine(programFiles, "WSL", "wsl.exe");
            if (File.Exists(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (info.FileMajorPart > 0)
                    return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            }
            return apps?.FirstOrDefault(a => string.Equals(a.Name, WslNames.StorePackage, StringComparison.OrdinalIgnoreCase))?.Version;
        }
    }
}
