using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    /// <summary>
    /// Liest die App-Pakete (MSIX/AppX) aus der Registrierung und ihren Manifesten: das geht als Lokales System ohne
    /// PowerShell-Modul und ohne angemeldeten Benutzer. Für welche Benutzer ein Paket installiert ist, steht dort nicht verlässlich.
    /// </summary>
    internal static class AppCollector
    {
        private const string Repository = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages";
        private const string AllUserStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";

        public static List<AppItem> Read()
        {
            var apps = new List<AppItem>();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var repository = hklm.OpenSubKey(Repository))
            using (var store = hklm.OpenSubKey(AllUserStore))
            {
                if (repository == null)
                    return apps;
                // Bereitgestellt ist meist das Bündel in der Version des Abbilds: verglichen wird deshalb Name + Herausgeberkennung.
                var provisioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var applications = store?.OpenSubKey("Applications"))
                {
                    foreach (var fullName in applications?.GetSubKeyNames() ?? new string[0])
                    {
                        if (AppNames.TryParseFullName(fullName, out var name, out _, out _, out _, out var publisherId))
                            provisioned.Add(name + "_" + publisherId);
                    }
                }

                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
                foreach (var fullName in repository.GetSubKeyNames())
                {
                    // Nur Hauptpakete: Sprach- und Skalierungspakete tragen eine Ressourcenkennung, Bündel eine Tilde.
                    if (!AppNames.TryParseFullName(fullName, out var name, out var version, out var architecture, out var resourceId, out var publisherId) || resourceId.Length > 0)
                        continue;
                    string? path;
                    using (var key = repository.OpenSubKey(fullName))
                        path = key?.GetValue("Path") as string;
                    // Bestandteile von Windows selbst (Startmenü, Einstellungen …) lassen sich nicht entfernen und wären nur Rauschen.
                    if (path == null || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var app = new AppItem { Name = name, Version = version, Architecture = architecture, Provisioned = provisioned.Contains(name + "_" + publisherId) };
                    if (!ReadManifest(path, app))
                        continue;
                    apps.Add(app);
                }
            }
            // Mehrere Versionen oder Architekturen nebeneinander (auch Reste älterer Stände): je Name die neueste.
            return apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var newest = g.OrderByDescending(a => ParseVersion(a.Version)).First();
                    newest.Provisioned = g.Any(a => a.Provisioned);
                    return newest;
                })
                .OrderBy(a => a.DisplayName ?? a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static Version ParseVersion(string? version) => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0);

        /// <summary>Ergänzt Anzeigename und Herausgeber; false = Framework-Paket (Laufzeitbibliothek), das nicht in die Liste gehört.</summary>
        private static bool ReadManifest(string path, AppItem app)
        {
            try
            {
                var manifest = new XmlDocument { XmlResolver = null };
                manifest.Load(Path.Combine(path, "AppxManifest.xml"));
                var properties = manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']");
                if (string.Equals(Text(properties, "Framework"), "true", StringComparison.OrdinalIgnoreCase))
                    return false;
                app.DisplayName = Plain(Text(properties, "DisplayName"));
                app.Publisher = Plain(Text(properties, "PublisherDisplayName"))
                                ?? CommonName((manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']") as XmlElement)?.GetAttribute("Publisher"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
            {
                // Ohne lesbares Manifest bleibt der Paketname; bekannte Laufzeitbibliotheken verrät schon ihr Name.
                if (app.Name.StartsWith("Microsoft.VCLibs.", StringComparison.OrdinalIgnoreCase) || app.Name.StartsWith("Microsoft.NET.", StringComparison.OrdinalIgnoreCase)
                    || app.Name.StartsWith("Microsoft.UI.Xaml.", StringComparison.OrdinalIgnoreCase) || app.Name.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static string? Text(XmlNode? parent, string name) => parent?.SelectSingleNode("*[local-name()='" + name + "']")?.InnerText;

        /// <summary>Verweise auf Ressourcen (ms-resource:…) sind ohne die Sprachdateien des Pakets kein lesbarer Text.</summary>
        private static string? Plain(string? value)
        {
            var text = value?.Trim();
            return string.IsNullOrEmpty(text) || text!.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : text;
        }

        /// <summary>"CN=Contoso Ltd, O=…" → "Contoso Ltd".</summary>
        private static string? CommonName(string? distinguishedName)
        {
            if (string.IsNullOrWhiteSpace(distinguishedName))
                return null;
            foreach (var part in distinguishedName!.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(3).Trim().Trim('"');
            }
            return null;
        }
    }
}
