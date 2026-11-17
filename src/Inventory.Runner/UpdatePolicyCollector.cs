using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    /// <summary>
    /// Die wirksame Windows-Update-Richtlinie: die Werte aus den Richtlinienschlüsseln, zu jedem die GPO, aus der er stammt
    /// (RSoP, nur als Administrator bzw. Lokales System lesbar), die Gruppen, mit denen der Computer die Gruppenrichtlinie
    /// zuletzt angewendet hat, und die Update-Richtlinie aus Intune. Nur lesend.
    /// </summary>
    internal static class UpdatePolicyCollector
    {
        private const string RsopScope = @"root\rsop\computer";
        /// <summary>Gesamtlauf der Gruppenrichtlinie für den Computer (die Null-GUID steht für die Verarbeitung als Ganzes).</summary>
        private const string MachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Extension-List\{00000000-0000-0000-0000-000000000000}";
        private const int MaxValues = 100;
        private const int MaxGroups = 200;

        public static UpdatePolicyInfo Read(List<string> warnings)
        {
            var info = new UpdatePolicyInfo();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                ReadValues(hklm, UpdatePolicyKeys.PolicyPath, UpdatePolicyKeys.WindowsUpdate, UpdatePolicySources.Registry, info.Settings);
                ReadValues(hklm, UpdatePolicyKeys.PolicyPath + @"\" + UpdatePolicyKeys.AutoUpdate, UpdatePolicyKeys.AutoUpdate, UpdatePolicySources.Registry, info.Settings);
                ReadValues(hklm, UpdatePolicyKeys.MdmPath, UpdatePolicyKeys.Mdm, UpdatePolicySources.Mdm, info.Settings);
                info.GroupPolicyAppliedUtc = AppliedUtc(hklm);
            }
            try
            {
                ReadRsop(info);
            }
            catch (Exception ex) when (ex is ManagementException || ex is UnauthorizedAccessException || ex is COMException)
            {
                // Die Werte gelten trotzdem; nur ihre Herkunft und die Gruppen fehlen.
                warnings.Add("Update-Richtlinie (RSoP): " + ex.Message);
            }
            return info;
        }

        private static void ReadValues(RegistryKey hklm, string path, string key, string source, List<UpdatePolicySetting> settings)
        {
            using (var policy = hklm.OpenSubKey(path))
            {
                if (policy == null)
                    return;
                // Im Schlüssel von Intune stehen zu jedem Wert Angaben, wer ihn gesetzt hat; das sind keine Einstellungen.
                var names = policy.GetValueNames().Where(n => !string.IsNullOrEmpty(n)
                    && !n.EndsWith("_ProviderSet", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("_WinningProvider", StringComparison.OrdinalIgnoreCase));
                foreach (var name in names.Take(MaxValues))
                {
                    settings.Add(new UpdatePolicySetting
                    {
                        Source = source, Key = key, Name = name,
                        Value = RsopValues.Format(policy.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)),
                    });
                }
            }
        }

        private static DateTime? AppliedUtc(RegistryKey hklm)
        {
            using (var key = hklm.OpenSubKey(MachineRunKey))
            {
                if (key?.GetValue("EndTimeHi") is int high && key.GetValue("EndTimeLo") is int low)
                {
                    var fileTime = ((long)(uint)high << 32) | (uint)low;
                    if (fileTime > 0)
                        return DateTime.FromFileTimeUtc(fileTime);
                }
            }
            return null;
        }

        /// <summary>
        /// RSoP hält zu jedem Wert einer administrativen Vorlage alle GPOs fest, die ihn setzen; Rang 1 gewinnt. Ohne Sitzung
        /// (RSoP-Protokollierung abgeschaltet) bleibt die Herkunft offen.
        /// </summary>
        private static void ReadRsop(UpdatePolicyInfo info)
        {
            var sessions = Query("SELECT SecurityGroups FROM RSOP_Session");
            if (sessions.Count == 0)
                return;
            info.RsopRead = true;
            info.Groups = Groups(sessions[0]["SecurityGroups"] as string[]);

            var gpos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gpo in Query("SELECT id, name FROM RSOP_GPO"))
            {
                if (Text(gpo, "id") is string id && !gpos.ContainsKey(id))
                    gpos[id] = Text(gpo, "name") ?? id;
            }

            // Ohne WHERE: LIKE mit Backslashes ist in WQL fehleranfällig, und die Liste ist überschaubar.
            var rows = Query("SELECT registryKey, valueName, valueType, value, GPOID, precedence FROM RSOP_RegistryPolicySetting")
                .Select(o => new
                {
                    Key = UpdatePolicyKeys.FromPolicyPath(Text(o, "registryKey")),
                    Name = Text(o, "valueName"),
                    Value = RsopValues.Decode(o["value"] as byte[], Convert.ToUInt32(o["valueType"] ?? 0u)),
                    Gpo = Text(o, "GPOID") is string id ? (gpos.TryGetValue(id, out var name) ? name : id) : null,
                    Precedence = Convert.ToInt32(o["precedence"] ?? 0),
                })
                // "**del.Name" und Ähnliches: Die GPO löscht einen Wert, statt ihn zu setzen.
                .Where(r => r.Key != null && r.Name != null && !r.Name.StartsWith("**", StringComparison.Ordinal) && r.Gpo != null)
                .ToList();

            foreach (var setting in info.Settings.Where(s => s.Source == UpdatePolicySources.Registry))
            {
                var matches = rows.Where(r => r.Key == setting.Key && string.Equals(r.Name, setting.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.Precedence).ToList();
                if (matches.Count == 0)
                    continue;
                setting.Source = UpdatePolicySources.Gpo;
                setting.Gpo = matches[0].Gpo;
                setting.AlsoSetBy = matches.Skip(1).Select(m => new UpdatePolicyOverride { Gpo = m.Gpo!, Value = m.Value }).ToList();
            }
        }

        /// <summary>Nur Domänengruppen (und Gruppen des eigenen Computers); Jeder, Authentifizierte Benutzer usw. sagen nichts.</summary>
        private static List<string> Groups(string[]? sids)
        {
            var names = new List<string>();
            foreach (var sid in (sids ?? new string[0]).Where(s => s != null && s.StartsWith("S-1-5-21-", StringComparison.Ordinal)).Distinct())
            {
                try
                {
                    var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
                    names.Add(account.Substring(account.IndexOf('\\') + 1));
                }
                catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException)
                {
                    // Kein Domänencontroller erreichbar oder die Gruppe ist gelöscht: dann die SID.
                    names.Add(sid);
                }
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(MaxGroups).ToList();
        }

        private static List<ManagementBaseObject> Query(string wql)
        {
            using (var searcher = new ManagementObjectSearcher(RsopScope, wql))
            using (var results = searcher.Get())
                return results.Cast<ManagementBaseObject>().ToList();
        }

        private static string? Text(ManagementBaseObject o, string property)
        {
            var value = o[property]?.ToString()?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
