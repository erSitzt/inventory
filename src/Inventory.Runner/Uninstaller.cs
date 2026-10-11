using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    /// <summary>Findet installierte Programme in den Uninstall-Schlüsseln und baut den Befehl für eine stille Deinstallation.</summary>
    internal static class Uninstaller
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        /// <summary>Ein Namensmuster, das mehr Programme trifft, ist fast sicher ein Versehen ("Microsoft").</summary>
        private const int MaxMatches = 3;

        internal sealed class Entry
        {
            public RegistryHive Hive;
            public RegistryView View;
            public string Path = "";
            public string KeyName = "";
            public string Name = "";
            public string? Version;
            public string? UninstallString;
            public string? QuietUninstallString;
            public bool WindowsInstaller;
            /// <summary>Gesetzt = nur für diesen Benutzer installiert.</summary>
            public string? UserSid;

            public bool IsMsi => WindowsInstaller && Regex.IsMatch(KeyName, @"^\{[0-9A-Fa-f-]{36}\}$");
        }

        /// <exception cref="InvalidDataException">Der Schritt nennt kein Programm oder das Muster trifft zu viele.</exception>
        public static List<Entry> Find(JobStep step)
        {
            var byCode = !string.IsNullOrWhiteSpace(step.ProductCode);
            if (!byCode && string.IsNullOrWhiteSpace(step.SoftwareName))
                throw new InvalidDataException("Neither a program name nor a product code is given");
            var code = byCode ? SoftwareMatcher.NormalizeProductCode(step.ProductCode!) : null;

            var found = All().Where(e => byCode
                ? string.Equals(e.KeyName, code, StringComparison.OrdinalIgnoreCase)
                : SoftwareMatcher.Matches(step.SoftwareName, null, e.Name, e.Version)).ToList();
            if (!byCode && found.Count > MaxMatches)
            {
                throw new InvalidDataException($"\"{step.SoftwareName}\" matches {found.Count} programs ({string.Join(", ", found.Take(6).Select(e => e.Name))}" +
                                               $"{(found.Count > 6 ? ", …" : "")}). Nothing was uninstalled; make the name more specific.");
            }
            return found;
        }

        private static List<Entry> All()
        {
            var entries = new List<Entry>();
            Read(entries, RegistryHive.LocalMachine, RegistryView.Registry64, UninstallKey, null);
            if (Environment.Is64BitOperatingSystem)
                Read(entries, RegistryHive.LocalMachine, RegistryView.Registry32, UninstallKey, null);
            using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            {
                foreach (var sid in users.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !n.EndsWith("_Classes", StringComparison.Ordinal)))
                    Read(entries, RegistryHive.Users, RegistryView.Registry64, sid + "\\" + UninstallKey, sid);
            }
            return entries;
        }

        private static void Read(List<Entry> entries, RegistryHive hive, RegistryView view, string path, string? userSid)
        {
            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var uninstall = root.OpenSubKey(path))
            {
                if (uninstall == null)
                    return;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using (var key = uninstall.OpenSubKey(name))
                    {
                        // Wie "Programme und Features": Einträge ohne Namen, Systemkomponenten und Unterpakete zählen nicht.
                        if (!(key?.GetValue("DisplayName") is string displayName) || string.IsNullOrWhiteSpace(displayName))
                            continue;
                        if ((key.GetValue("SystemComponent") is int system && system == 1) || key.GetValue("ParentKeyName") != null)
                            continue;
                        entries.Add(new Entry
                        {
                            Hive = hive, View = view, Path = path + "\\" + name, KeyName = name, Name = displayName.Trim(),
                            Version = (key.GetValue("DisplayVersion") as string)?.Trim(),
                            UninstallString = key.GetValue("UninstallString") as string,
                            QuietUninstallString = key.GetValue("QuietUninstallString") as string,
                            WindowsInstaller = key.GetValue("WindowsInstaller") is int msi && msi == 1,
                            UserSid = userSid,
                        });
                    }
                }
            }
        }

        /// <summary>Befehlszeile für die stille Deinstallation.</summary>
        /// <exception cref="InvalidOperationException">Für dieses Setup ist kein Parameter für die stille Deinstallation bekannt.</exception>
        public static string Command(Entry entry, string? arguments)
        {
            var extra = string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments!.Trim();
            if (entry.IsMsi)
                return $"msiexec.exe /x {entry.KeyName} /qn /norestart{extra}";

            // Eigene Parameter ersetzen die eingetragenen: wer sie angibt, kennt das Setup.
            if (extra.Length > 0)
                return $"\"{Executable(entry)}\"{extra}";
            // Inno Setup trägt als stillen Befehl nur /SILENT ein; das zeigt noch ein Fortschrittsfenster und kann Rückfragen stellen.
            var inno = !string.IsNullOrWhiteSpace(entry.UninstallString) && IsInnoUninstaller(Executable(entry));
            if (!inno && !string.IsNullOrWhiteSpace(entry.QuietUninstallString))
                return entry.QuietUninstallString!.Trim();

            var exe = Executable(entry);
            switch (inno ? "inno" : Kind(exe))
            {
                case "inno":
                    return $"\"{exe}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
                case "nullsoft":
                    // _?= hält den Uninstaller im Vordergrund; ohne kopiert er sich weg und kehrt sofort zurück. Muss am Ende und ohne Anführungszeichen stehen.
                    return $"\"{exe}\" /S _?={System.IO.Path.GetDirectoryName(exe)}";
                default:
                    throw new InvalidOperationException(
                        $"No silent uninstall is known for {entry.Name} (registered command: {entry.UninstallString}). " +
                        "Enter the parameters for a silent uninstall in the step.");
            }
        }

        private static string Executable(Entry entry)
        {
            var command = (entry.UninstallString ?? "").Trim();
            if (command.Length == 0)
                throw new InvalidOperationException($"{entry.Name} has no uninstall command registered");
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = command.IndexOf('"', 1);
                if (end > 1)
                    return Environment.ExpandEnvironmentVariables(command.Substring(1, end - 1));
            }
            var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0)
                throw new InvalidOperationException($"The uninstall command of {entry.Name} cannot be understood: {command}");
            return Environment.ExpandEnvironmentVariables(command.Substring(0, exe + 4));
        }

        private static bool IsInnoUninstaller(string exe) =>
            Regex.IsMatch(System.IO.Path.GetFileName(exe), @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase);

        /// <summary>Erkennt Inno Setup und NSIS an Kennungen in der Programmdatei; alles andere bleibt unbekannt.</summary>
        private static string Kind(string exe)
        {
            if (!File.Exists(exe))
                throw new InvalidOperationException($"The uninstaller {exe} does not exist");
            if (IsInnoUninstaller(exe))
                return "inno";
            var buffer = new byte[(int)Math.Min(new FileInfo(exe).Length, 16 * 1024 * 1024)];
            using (var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
            }
            // Latin-1 bildet jedes Byte auf ein Zeichen ab; die Kennungen stehen als ASCII oder UTF-16 in der Datei.
            var text = Encoding.GetEncoding(28591).GetString(buffer);
            if (text.Contains("Nullsoft") || text.Contains("N\0u\0l\0l\0s\0o\0f\0t"))
                return "nullsoft";
            if (text.Contains("Inno Setup") || text.Contains("I\0n\0n\0o\0 \0S\0e\0t\0u\0p"))
                return "inno";
            return "";
        }

        /// <summary>Manche Uninstaller arbeiten im Hintergrund weiter; erst wenn der Eintrag fehlt, ist das Programm wirklich weg.</summary>
        public static bool WaitUntilGone(Entry entry, int seconds, Func<bool> cancelled)
        {
            for (var waited = 0; ; waited++)
            {
                using (var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View))
                using (var key = root.OpenSubKey(entry.Path))
                {
                    if (key == null)
                        return true;
                }
                if (waited >= seconds * 2 || cancelled())
                    return false;
                Thread.Sleep(500);
            }
        }

        /// <summary>SID eines Kontos im Format DOMÄNE\Name; null, wenn es sich nicht auflösen lässt.</summary>
        public static string? Sid(string userName)
        {
            try
            {
                return new NTAccount(userName).Translate(typeof(SecurityIdentifier)).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException)
            {
                return null;
            }
        }
    }
}
