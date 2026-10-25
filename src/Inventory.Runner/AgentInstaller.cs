using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Inventory.Contracts;

namespace Inventory.Runner
{
    /// <summary>
    /// Richtet den Agenten aus einem Ordner heraus ein (Agent-Setup des Servers, z. B. als Startskript einer
    /// Gruppenrichtlinie oder über eine vorhandene Softwareverteilung) – ohne dass der Server den Rechner erreichen muss.
    /// </summary>
    internal static class AgentInstaller
    {
        private const string FolderName = "Inventory Agent";
        private const int NotElevated = 5;

        private static string TargetDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FolderName); }
        }

        public static int Install(string sourceDir, bool force)
        {
            if (!IsElevated())
            {
                Console.Error.WriteLine("The agent can only be installed with administrator rights (or as Local System).");
                return NotElevated;
            }

            sourceDir = Path.GetFullPath(sourceDir);
            var target = TargetDir;
            if (!File.Exists(Path.Combine(sourceDir, AgentProtocol.ConfigFile)))
            {
                Console.Error.WriteLine("No " + AgentProtocol.ConfigFile + " next to the setup: download the agent setup from the Inventory server.");
                return 2;
            }
            if (string.Equals(sourceDir.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("The setup must not be started from the installation folder.");
                return 2;
            }

            var installed = ServiceExists() && File.Exists(Path.Combine(target, JobFiles.RunnerExe)) && File.Exists(Path.Combine(target, AgentProtocol.ConfigFile));
            if (installed && !force)
            {
                // Ein eingerichteter Agent aktualisiert sich selbst vom Server. Das Setup darf ihn nicht bei jedem Start
                // auf seinen (dann älteren) Stand zurücksetzen.
                Start();
                Console.WriteLine("Agent already installed: " + target);
                return 0;
            }

            Stop();
            // Auch ein schon vorhandener Ordner (von Hand angelegt, Rest einer alten Installation) wird abgeschottet,
            // bevor das Anmeldegeheimnis hineinkommt.
            ProtectDirectory(target);
            foreach (var file in Directory.GetFiles(sourceDir).Where(IsAgentFile))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);

            if (!ServiceExists())
            {
                var binPath = "\\\"" + Path.Combine(target, JobFiles.RunnerExe) + "\\\" " + RunnerModes.Agent;
                if (Sc("create " + AgentProtocol.ServiceName + " binPath= \"" + binPath + "\" start= auto DisplayName= \"Inventory Agent\"") != 0)
                {
                    Console.Error.WriteLine("The service " + AgentProtocol.ServiceName + " could not be created.");
                    return 1;
                }
                Sc("description " + AgentProtocol.ServiceName + " \"Runs inventory scans and deployment jobs for the Inventory server.\"");
            }
            // Der erste Neustart kommt schnell: so startet sich der Agent nach einer Selbstaktualisierung neu.
            Sc("failure " + AgentProtocol.ServiceName + " reset= 3600 actions= restart/5000/restart/60000/restart/300000");
            Sc("failureflag " + AgentProtocol.ServiceName + " 1");
            Start();
            Console.WriteLine("Agent installed: " + target);
            return 0;
        }

        public static int Uninstall()
        {
            if (!IsElevated())
            {
                Console.Error.WriteLine("The agent can only be removed with administrator rights (or as Local System).");
                return NotElevated;
            }
            if (ServiceExists())
            {
                Stop();
                if (Sc("delete " + AgentProtocol.ServiceName) != 0)
                {
                    // Der Ordner bleibt, sonst zeigte der Dienst auf ein Programm, das es nicht mehr gibt.
                    Console.Error.WriteLine("The service " + AgentProtocol.ServiceName + " could not be deleted.");
                    return 1;
                }
            }
            var target = TargetDir;
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("The folder " + target + " could not be removed completely: " + ex.Message);
                return 1;
            }
            Console.WriteLine("Agent removed.");
            return 0;
        }

        /// <summary>Programmdateien, Konfiguration und Versionskennung; nicht das Startskript, Hinweise oder ein fremder Schlüssel.</summary>
        internal static bool IsAgentFile(string path)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, AgentProtocol.ConfigFile, StringComparison.OrdinalIgnoreCase) || string.Equals(name, AgentProtocol.VersionFile, StringComparison.OrdinalIgnoreCase))
                return true;
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".exe":
                case ".dll":
                    return true;
                case ".config":
                    return name.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        private static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Im Ordner liegen Anmeldegeheimnis und Schlüssel, und der Dienst läuft als SYSTEM: nur SYSTEM und Administratoren.</summary>
        private static void ProtectDirectory(string path)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            // DirectoryInfo bietet beides unter net48 und modernem .NET (dort als Erweiterungsmethoden).
            var directory = new DirectoryInfo(path);
            if (directory.Exists)
                directory.SetAccessControl(security);
            else
                directory.Create(security);
        }

        private static bool ServiceExists()
        {
            return ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, AgentProtocol.ServiceName, StringComparison.OrdinalIgnoreCase));
        }

        private static void Stop()
        {
            if (!ServiceExists())
                return;
            using (var service = new ServiceController(AgentProtocol.ServiceName))
            {
                if (service.Status == ServiceControllerStatus.Stopped)
                    return;
                if (service.Status != ServiceControllerStatus.StopPending)
                    service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
        }

        private static void Start()
        {
            using (var service = new ServiceController(AgentProtocol.ServiceName))
            {
                if (service.Status == ServiceControllerStatus.Stopped)
                    service.Start();
            }
        }

        private static int Sc(string arguments)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var process = Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode;
            }
        }
    }
}
