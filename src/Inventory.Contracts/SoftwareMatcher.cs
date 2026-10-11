using System;
using System.Text.RegularExpressions;

namespace Inventory.Contracts
{
    /// <summary>Vergleicht einen Eintrag aus "Programme und Features" mit dem Namensmuster einer Bedingung.</summary>
    public static class SoftwareMatcher
    {
        /// <param name="pattern">
        /// Ohne * muss das Muster als ganze Wörter im Namen vorkommen ("Git" trifft "Git 2.45", nicht "GitHub Desktop").
        /// Mit * gilt es für den ganzen Namen ("7-Zip*").
        /// </param>
        /// <param name="minVersion">Leer = jede Version.</param>
        public static bool Matches(string? pattern, string? minVersion, string? name, string? version)
        {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(name))
                return false;
            var escaped = Regex.Escape(pattern!.Trim());
            var expression = pattern.IndexOf('*') >= 0
                ? "^" + escaped.Replace("\\*", ".*") + "$"
                : "(?<![\\p{L}\\p{N}])" + escaped + "(?![\\p{L}\\p{N}])";
            if (!Regex.IsMatch(name!.Trim(), expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;
            return string.IsNullOrWhiteSpace(minVersion) || VersionComparer.Compare(version, minVersion) >= 0;
        }

        /// <summary>
        /// Name ohne Versionsnummer und Zusätze in Klammern ("7-Zip 26.04 (x64 edition)" → "7-Zip"), damit eine Erkennung
        /// auch spätere Versionen trifft.
        /// </summary>
        public static string BaseName(string name)
        {
            var result = Regex.Replace(name.Trim(), @"\s+v?\d+(\.\d+)+.*$|\s*\([^)]*\)\s*$", "").Trim(' ', '-', '–');
            return result.Length > 0 ? result : name.Trim();
        }

        /// <summary>Produktcode in der Schreibweise der Registry: Großbuchstaben in geschweiften Klammern.</summary>
        public static string NormalizeProductCode(string code) =>
            "{" + code.Trim().Trim('{', '}').ToUpperInvariant() + "}";
    }
}
