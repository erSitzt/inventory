using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Inventory.Contracts;
using Microsoft.Win32;

namespace Inventory.Runner
{
    /// <summary>Sucht, lädt und installiert ausstehende Updates über den Windows-Update-Agenten (WSUS oder Microsoft Update, wie auf dem Rechner eingestellt).</summary>
    internal static class WindowsUpdateInstaller
    {
        /// <summary>Exitcode wie bei Installern: 0 = fertig, 3010 = fertig, Neustart nötig.</summary>
        public const int RebootRequiredExitCode = 3010;

        private const int DefaultTimeoutSeconds = 7200;
        private const string SecurityUpdates = "0FA1201D-4330-4FA8-8AE9-B877473B6441";
        private const string CriticalUpdates = "E6CF1350-C01B-414D-A61F-263D14D133B4";

        public sealed class Outcome
        {
            public int ExitCode;
            public string? Error;
        }

        /// <param name="say">Nimmt Fortschrittszeilen entgegen; wird aus einem anderen Thread aufgerufen.</param>
        /// <param name="cancelled">true, sobald der Job abgebrochen wurde.</param>
        public static Outcome Run(JobStep step, Action<string> say, Func<bool> cancelled)
        {
            // Der Update-Agent verweigert Download und Installation aus einer Netzwerkanmeldung heraus (WinRM): das geht nur lokal als System.
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!identity.IsSystem)
                    return new Outcome { ExitCode = 1, Error = "Windows updates can only be installed as Local System (SMB transport or agent), not over WinRM." };
            }

            // Eine Liste, aus der keine KB-Nummer zu lesen ist, darf nicht zu "alles installieren" werden.
            if (step.UpdateKbIds.Count > 0 && step.UpdateKbIds.All(k => NormalizeKb(k).Length == 0))
                return new Outcome { ExitCode = 1, Error = "The step names KB numbers, but none of them is valid: " + string.Join(", ", step.UpdateKbIds) };

            var outcome = new Outcome();
            var stop = false;
            var detached = false;
            // Nach Abbruch oder Zeitüberschreitung gehört der Fortschritts-Kanal nicht mehr diesem Schritt.
            Action<string> report = line =>
            {
                if (!Volatile.Read(ref detached))
                    say(line);
            };
            var worker = new Thread(() =>
            {
                try
                {
                    Install(step, report, () => Volatile.Read(ref stop), outcome);
                }
                catch (Exception ex)
                {
                    outcome.ExitCode = 1;
                    outcome.Error = Describe(ex);
                }
            })
            { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();

            var limit = DateTime.UtcNow.AddSeconds(step.TimeoutSeconds > 0 ? step.TimeoutSeconds : DefaultTimeoutSeconds);
            string? interrupted = null;
            while (!worker.Join(500))
            {
                if (cancelled())
                    interrupted = "Cancelled";
                else if (DateTime.UtcNow > limit)
                    interrupted = "Timeout: the updates did not finish in time";
                if (interrupted != null)
                    break;
            }
            if (interrupted == null)
                return outcome;

            // Ein laufender Aufruf des Agenten lässt sich nicht abbrechen: nach dem aktuellen Update ist Schluss. Dauert auch
            // das zu lange, läuft es im Hintergrund zu Ende; Windows selbst bleibt dabei in einem sauberen Zustand.
            Volatile.Write(ref stop, true);
            worker.Join(TimeSpan.FromMinutes(2));
            Volatile.Write(ref detached, true);
            return new Outcome { ExitCode = 1, Error = interrupted };
        }

        private static void Install(JobStep step, Action<string> say, Func<bool> stop, Outcome outcome)
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
            session.ClientApplicationID = "Inventory";
            dynamic searcher = session.CreateUpdateSearcher();
            var criteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";
            if (step.UpdateIncludeDrivers)
                criteria += " or IsInstalled=0 and IsHidden=0 and Type='Driver'";
            say("Searching for updates …");
            dynamic found = searcher.Search(criteria);

            var wanted = new List<object>();
            var kbFilter = new HashSet<string>(step.UpdateKbIds.Select(NormalizeKb).Where(k => k.Length > 0), StringComparer.OrdinalIgnoreCase);
            foreach (dynamic update in found.Updates)
            {
                var title = (string)update.Title;
                var kbs = new List<string>();
                foreach (var kb in update.KBArticleIDs)
                    kbs.Add("KB" + (string)kb);
                if (kbFilter.Count > 0 && !kbs.Any(kbFilter.Contains))
                    continue;
                if (step.UpdateSecurityOnly && kbFilter.Count == 0 && !IsSecurity(update))
                    continue;
                // Ein Update, das Eingaben verlangt, bliebe ohne Benutzer für immer stehen.
                if ((bool)update.InstallationBehavior.CanRequestUserInput)
                {
                    say($"Skipped (needs user input): {title}");
                    continue;
                }
                wanted.Add(update);
                if (step.UpdateMaxCount > 0 && wanted.Count >= step.UpdateMaxCount)
                    break;
            }

            if (wanted.Count == 0)
            {
                say(kbFilter.Count > 0 ? "None of the requested updates is pending – nothing to do" : "No applicable updates – nothing to do");
                outcome.ExitCode = PendingReboot() ? RebootRequiredExitCode : 0;
                return;
            }
            say($"{wanted.Count} update(s) to install");

            var failed = new List<string>();
            var reboot = false;
            var done = 0;
            foreach (dynamic update in wanted)
            {
                if (stop())
                    break;
                var title = (string)update.Title;
                dynamic single = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true));
                if (!(bool)update.EulaAccepted)
                    update.AcceptEula();
                single.Add(update);

                if (!(bool)update.IsDownloaded)
                {
                    say($"Downloading: {title}");
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = single;
                    dynamic download = downloader.Download();
                    if (!Succeeded((int)download.ResultCode))
                    {
                        failed.Add(title);
                        say($"Download failed (0x{(int)download.HResult:X8}): {title}");
                        continue;
                    }
                }

                say($"Installing: {title}");
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = single;
                dynamic result = installer.Install();
                reboot |= (bool)result.RebootRequired;
                if (Succeeded((int)result.ResultCode))
                {
                    done++;
                    say($"Installed: {title}");
                }
                else
                {
                    failed.Add(title);
                    say($"Installation failed (0x{(int)result.GetUpdateResult(0).HResult:X8}): {title}");
                }
            }

            say($"{done} of {wanted.Count} update(s) installed" + (reboot ? "; a restart is required" : ""));
            if (failed.Count > 0)
            {
                outcome.ExitCode = 1;
                outcome.Error = $"{failed.Count} update(s) failed: {string.Join("; ", failed)}";
            }
            else
            {
                outcome.ExitCode = reboot || PendingReboot() ? RebootRequiredExitCode : 0;
            }
        }

        /// <summary>OperationResultCode: 2 = erfolgreich, 3 = erfolgreich mit Fehlern.</summary>
        private static bool Succeeded(int resultCode) => resultCode == 2 || resultCode == 3;

        private static bool IsSecurity(dynamic update)
        {
            if (!string.IsNullOrWhiteSpace((string)update.MsrcSeverity))
                return true;
            foreach (dynamic category in update.Categories)
            {
                var id = ((string)category.CategoryID).ToUpperInvariant();
                if ((string)category.Type == "UpdateClassification" && (id == SecurityUpdates || id == CriticalUpdates))
                    return true;
            }
            return false;
        }

        private static string NormalizeKb(string value)
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? "" : "KB" + digits;
        }

        /// <summary>Registry-Schlüssel, deren bloßes Vorhandensein einen ausstehenden Neustart anzeigt; auch der Inventar-Scan wertet sie aus.</summary>
        internal static readonly string[] RebootKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\PostRebootReporting",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending",
        };

        /// <summary>Steht ein Neustart aus? Der Update-Agent weiß es am verlässlichsten; die Registry-Schlüssel ergänzen das.</summary>
        public static bool PendingReboot()
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                foreach (var path in RebootKeys)
                {
                    using (var key = hklm.OpenSubKey(path))
                    {
                        if (key != null)
                            return true;
                    }
                }
            }
            try
            {
                dynamic systemInfo = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", true));
                return (bool)systemInfo.RebootRequired;
            }
            catch (Exception ex) when (ex is COMException || ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return false;
            }
        }

        private static string Describe(Exception ex) =>
            ex is COMException com ? $"Windows Update Agent error 0x{com.ErrorCode:X8}: {com.Message}" : ex.Message;
    }
}
