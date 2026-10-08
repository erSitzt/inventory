using System;
using System.Text.RegularExpressions;

namespace Inventory.Launcher
{
    /// <summary>Prüft inventory://-Links und übersetzt sie in den Aufruf eines Windows-Programms.</summary>
    internal static class LinkParser
    {
        internal const string Scheme = "inventory";
        private static readonly Regex HostPattern = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9._-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);

        /// <summary>Übersetzt einen Link in Programm und Parameter; lehnt alles ab, was nicht exakt dem erwarteten Muster entspricht.</summary>
        internal static bool TryParse(string link, out string fileName, out string arguments, out string error)
        {
            fileName = arguments = error = "";
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            {
                error = "kein inventory-Link";
                return false;
            }

            // inventory://aktion/rechner[?session=n]
            var action = uri.Host.ToLowerInvariant();
            var host = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (!HostPattern.IsMatch(host))
            {
                error = "ungültiger Rechnername";
                return false;
            }

            switch (action)
            {
                case "assist":
                    fileName = "msra.exe";
                    arguments = "/offerRA " + host;
                    return true;
                case "rdp":
                    fileName = "mstsc.exe";
                    arguments = "/v:" + host;
                    return true;
                case "shadow":
                    var query = uri.Query.TrimStart('?');
                    if (!query.StartsWith("session=", StringComparison.Ordinal) || !int.TryParse(query.Substring("session=".Length), out var session) || session < 0 || session > 65535)
                    {
                        error = "ungültige Sitzungsnummer";
                        return false;
                    }
                    fileName = "mstsc.exe";
                    arguments = "/v:" + host + " /shadow:" + session + " /control";
                    return true;
                default:
                    error = "unbekannte Aktion";
                    return false;
            }
        }
    }
}
