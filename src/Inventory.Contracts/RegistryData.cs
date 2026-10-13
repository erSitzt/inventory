using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Inventory.Contracts
{
    /// <summary>
    /// Liest die Angaben eines Registrierungs-Schritts: Struktur und Pfad aus dem Schlüssel, den Wert aus seinem Text.
    /// Runner und Server teilen sich die Prüfung, damit ein Schritt schon beim Speichern abgelehnt wird, nicht erst auf dem Rechner.
    /// </summary>
    public static class RegistryData
    {
        public enum Hive
        {
            LocalMachine,
            Users,
            ClassesRoot,
            CurrentUser,
        }

        /// <summary>Trennt "HKLM\SOFTWARE\Firma" in Struktur und Pfad; der Pfad ist bei einer bloßen Struktur leer.</summary>
        /// <exception cref="InvalidDataException">Der Schlüssel fehlt oder beginnt mit keiner bekannten Struktur.</exception>
        public static Hive Split(string? key, out string path)
        {
            var text = (key ?? "").Trim().Replace('/', '\\').Trim('\\');
            if (text.Length == 0)
                throw new InvalidDataException("Registrierungsschlüssel fehlt");
            var separator = text.IndexOf('\\');
            var root = (separator < 0 ? text : text.Substring(0, separator)).TrimEnd(':').ToUpperInvariant();
            path = separator < 0 ? "" : text.Substring(separator + 1).Trim('\\');
            switch (root)
            {
                case "HKLM":
                case "HKEY_LOCAL_MACHINE":
                    return Hive.LocalMachine;
                case "HKU":
                case "HKEY_USERS":
                    return Hive.Users;
                case "HKCR":
                case "HKEY_CLASSES_ROOT":
                    return Hive.ClassesRoot;
                case "HKCU":
                case "HKEY_CURRENT_USER":
                    return Hive.CurrentUser;
                default:
                    throw new InvalidDataException($"Unbekannte Registrierungsstruktur \"{root}\" (erwartet HKLM, HKCU, HKU oder HKCR)");
            }
        }

        /// <summary>Schlüssel, unter denen ganze Programme oder das System liegen: sie zu löschen ist nie gemeint.</summary>
        private static readonly string[] ProtectedKeys =
        {
            @"SOFTWARE\Microsoft", @"SOFTWARE\Classes", @"SOFTWARE\Policies", @"SOFTWARE\WOW6432Node", @"SOFTWARE\WOW6432Node\Microsoft",
            @"SOFTWARE\Microsoft\Windows", @"SOFTWARE\Microsoft\Windows NT", @"SOFTWARE\Microsoft\Windows\CurrentVersion", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            @"SOFTWARE\Policies\Microsoft", @"SYSTEM\CurrentControlSet", @"SYSTEM\CurrentControlSet\Services", @"SYSTEM\CurrentControlSet\Control", @"SYSTEM\CurrentControlSet\Enum",
        };

        /// <summary>
        /// true, wenn sich der Schlüssel samt Unterschlüsseln löschen lassen darf. Abgelehnt werden die Struktur selbst, ihre
        /// obersten Zweige (HKLM\SOFTWARE, HKCU\Software, HKU\&lt;Benutzer&gt;\Software …) und die Schlüssel, unter denen
        /// Windows und alle Programme eines Herstellers liegen. Geprüft wird der Pfad, wie er eingetragen ist – bevor HKCU auf
        /// die Struktur des Benutzers umgeschrieben wird.
        /// </summary>
        public static bool CanDeleteKey(Hive hive, string path)
        {
            var parts = new List<string>(path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries));
            // Unter HKEY_USERS steht zuerst der Benutzer; er zählt nicht als Ebene, und ihn selbst löscht niemand.
            if (hive == Hive.Users)
            {
                if (parts.Count == 0)
                    return false;
                parts.RemoveAt(0);
            }
            // HKCR ist flach (Dateiendungen und Klassen direkt unter der Struktur); überall sonst ist die erste Ebene ein ganzer Zweig.
            if (parts.Count < (hive == Hive.ClassesRoot ? 1 : 2))
                return false;
            var relative = string.Join("\\", parts.ToArray());
            foreach (var key in ProtectedKeys)
            {
                if (string.Equals(key, relative, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>Wandelt den Text in den Wert, den die Registrierung für diesen Typ erwartet (string, int, long, string[] oder byte[]).</summary>
        /// <exception cref="InvalidDataException">Der Text passt nicht zum Typ.</exception>
        public static object Convert(RegistryDataType type, string? data)
        {
            var text = data ?? "";
            switch (type)
            {
                case RegistryDataType.String:
                case RegistryDataType.ExpandString:
                    return text;
                case RegistryDataType.DWord:
                    return unchecked((int)(uint)Number(text, uint.MaxValue, "DWORD"));
                case RegistryDataType.QWord:
                    return unchecked((long)Number(text, ulong.MaxValue, "QWORD"));
                case RegistryDataType.MultiString:
                    return text.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                case RegistryDataType.Binary:
                    return Bytes(text);
                default:
                    throw new InvalidDataException($"Unbekannter Werttyp {type}");
            }
        }

        /// <summary>Dezimal oder mit 0x hexadezimal; auch negative DWORD-Angaben wie -1 sind üblich und meinen alle Bits gesetzt.</summary>
        private static ulong Number(string text, ulong max, string name)
        {
            var value = text.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(value.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && hex <= max)
                    return hex;
            }
            else if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= max)
                return number;
            else if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed) && signed < 0
                     && (max == ulong.MaxValue || signed >= int.MinValue))
                return max == ulong.MaxValue ? unchecked((ulong)signed) : unchecked((uint)(int)signed);
            throw new InvalidDataException($"\"{text}\" ist keine Zahl für einen {name}-Wert");
        }

        private static byte[] Bytes(string text)
        {
            var hex = text.Replace(" ", "").Replace(",", "").Replace("-", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");
            if (hex.Length % 2 != 0)
                throw new InvalidDataException("Binärdaten brauchen eine gerade Anzahl hexadezimaler Ziffern");
            var bytes = new List<byte>();
            for (var i = 0; i < hex.Length; i += 2)
            {
                if (!byte.TryParse(hex.Substring(i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException($"\"{hex.Substring(i, 2)}\" ist keine hexadezimale Ziffernfolge");
                bytes.Add(value);
            }
            return bytes.ToArray();
        }

        /// <summary>true, wenn der Name als Feature-Name an DISM gehen darf: Buchstaben, Ziffern, Punkt, Binde- und Unterstrich.</summary>
        public static bool IsFeatureName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name!.Length > 128)
                return false;
            foreach (var c in name)
            {
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '.' && c != '-' && c != '_')
                    return false;
            }
            return true;
        }
    }
}
