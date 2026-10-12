using System;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using Inventory.Contracts;

namespace Inventory.Runner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == RunnerModes.Version)
            {
                Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version);
                return 0;
            }

            if (args.Length == 1 && args[0] == RunnerModes.Service)
            {
                // Fest eingerichteter Dienst: die Job-ID kommt als Startargument vom Service Control Manager.
                ServiceBase.Run(new RunnerService(ResolveJobDir));
                return 0;
            }

            if (args.Length == 2 && args[0] == RunnerModes.Service)
            {
                ServiceBase.Run(new RunnerService(ResolveJobDir(args[1])));
                return 0;
            }

            if (args.Length == 2 && args[0] == RunnerModes.Console)
            {
                var result = new JobExecutor(ResolveJobDir(args[1])).Run();
                return result.Success ? 0 : 1;
            }

            if (args.Length == 1 && args[0] == RunnerModes.Agent)
            {
                ServiceBase.Run(new AgentService());
                return 0;
            }

            if (args.Length == 1 && args[0] == RunnerModes.AgentConsole)
            {
                var loop = new AgentLoop();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; loop.Stop(); };
                loop.Run();
                return 0;
            }

            if (args.Length >= 1 && args.Length <= 2 && args[0] == RunnerModes.InstallAgent && (args.Length == 1 || args[1] == RunnerModes.Force))
                return AgentInstaller.Install(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, args.Length == 2);

            if (args.Length == 1 && args[0] == RunnerModes.UninstallAgent)
                return AgentInstaller.Uninstall();

            Console.Error.WriteLine("Usage: InventoryRunner --service [<jobdir|job-id>] | --console <jobdir|job-id> | --agent | --agent-console | --install-agent [--force] | --uninstall-agent | --version");
            return 2;
        }

        /// <summary>
        /// Akzeptiert einen Pfad oder nur die Job-ID. Die ID wird relativ zur eigenen Ablage
        /// (…\Inventory\Runner\&lt;hash&gt;\InventoryRunner.exe → …\Inventory\Jobs\&lt;id&gt;) aufgelöst,
        /// damit der Server den lokalen Pfad von %SystemRoot% auf dem Zielrechner nicht kennen muss.
        /// </summary>
        private static string ResolveJobDir(string argument)
        {
            if (Path.IsPathRooted(argument))
                return argument;
            var runnerDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            return Path.GetFullPath(Path.Combine(runnerDir, "..", "..", JobFiles.JobsFolder, argument));
        }
    }
}
