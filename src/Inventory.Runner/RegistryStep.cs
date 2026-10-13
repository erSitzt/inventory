using System;
using System.IO;
using System.Security.Principal;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    /// <summary>Führt einen Registrierungs-Schritt aus.</summary>
    internal static class RegistryStep
    {
        /// <param name="userName">Für HKCU: das Konto des angemeldeten Benutzers (DOMÄNE\name); seine Struktur liegt unter HKEY_USERS.</param>
        /// <returns>Zusammenfassung für die Ausgabe des Schritts.</returns>
        public static string Run(JobStep step, string? userName)
        {
            var hive = RegistryData.Split(step.RegistryKey, out var path);
            // Am eingetragenen Pfad prüfen: bei HKCU steht später die Benutzerkennung davor, der Pfad wäre dann nie leer.
            if (path.Length == 0)
                throw new InvalidDataException("Der Schlüssel besteht nur aus der Struktur; ein Pfad darunter fehlt");
            // Der Schritt läuft im Prozess des Runners; der Benutzerkontext wählt nur die Struktur des Benutzers, nicht dessen Rechte.
            if (userName != null && hive != RegistryData.Hive.CurrentUser)
                throw new InvalidDataException("Der Kontext \"als angemeldeter Benutzer\" gilt nur für HKCU");
            // Ebenfalls am eingetragenen Pfad: die Benutzerkennung würde die Ebenen verschieben.
            if (step.RegistryAction == RegistryAction.DeleteKey && !RegistryData.CanDeleteKey(hive, path))
                throw new InvalidDataException("Dieser Schlüssel wird nicht gelöscht: er ist ein oberster Zweig der Registrierung oder enthält Windows bzw. alle Programme eines Herstellers");
            var view = step.Registry32Bit ? RegistryView.Registry32 : RegistryView.Registry64;
            RegistryHive root;
            switch (hive)
            {
                case RegistryData.Hive.LocalMachine:
                    root = RegistryHive.LocalMachine;
                    break;
                case RegistryData.Hive.ClassesRoot:
                    root = RegistryHive.ClassesRoot;
                    break;
                case RegistryData.Hive.Users:
                    root = RegistryHive.Users;
                    break;
                default:
                    // Der Runner läuft als Lokales System; dessen HKCU ist nicht die des Menschen am Rechner.
                    if (userName == null)
                        throw new InvalidDataException("HKCU braucht den Kontext \"als angemeldeter Benutzer\"");
                    var sid = ((SecurityIdentifier)new NTAccount(userName).Translate(typeof(SecurityIdentifier))).Value;
                    root = RegistryHive.Users;
                    path = sid + "\\" + path;
                    break;
            }

            var shown = step.RegistryKey!.Trim();
            var name = step.RegistryValueName ?? "";
            var label = name.Length == 0 ? "(Standard)" : name;
            using (var baseKey = RegistryKey.OpenBaseKey(root, view))
            {
                switch (step.RegistryAction)
                {
                    case RegistryAction.SetValue:
                        var value = RegistryData.Convert(step.RegistryDataType, step.RegistryData);
                        using (var key = baseKey.CreateSubKey(path, true))
                            key.SetValue(name, value, Kind(step.RegistryDataType));
                        return $"{shown}\\{label} gesetzt";
                    case RegistryAction.DeleteValue:
                        using (var key = baseKey.OpenSubKey(path, true))
                        {
                            if (key == null || key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == null)
                                return $"{shown}\\{label} war nicht vorhanden";
                            key.DeleteValue(name, false);
                        }
                        return $"{shown}\\{label} gelöscht";
                    case RegistryAction.DeleteKey:
                        using (var key = baseKey.OpenSubKey(path, false))
                        {
                            if (key == null)
                                return $"{shown} war nicht vorhanden";
                        }
                        baseKey.DeleteSubKeyTree(path, false);
                        return $"{shown} gelöscht";
                    default:
                        throw new InvalidDataException($"Unbekannte Aktion {step.RegistryAction}");
                }
            }
        }

        /// <summary>Ob der Schritt die Struktur des angemeldeten Benutzers meint; alles andere ist ungültig und wird in <see cref="Run"/> abgelehnt.</summary>
        public static bool IsUserKey(JobStep step)
        {
            try { return RegistryData.Split(step.RegistryKey, out _) == RegistryData.Hive.CurrentUser; }
            catch (InvalidDataException) { return false; }
        }

        private static RegistryValueKind Kind(RegistryDataType type)
        {
            switch (type)
            {
                case RegistryDataType.ExpandString: return RegistryValueKind.ExpandString;
                case RegistryDataType.DWord: return RegistryValueKind.DWord;
                case RegistryDataType.QWord: return RegistryValueKind.QWord;
                case RegistryDataType.MultiString: return RegistryValueKind.MultiString;
                case RegistryDataType.Binary: return RegistryValueKind.Binary;
                default: return RegistryValueKind.String;
            }
        }
    }
}
