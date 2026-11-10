using System.Runtime.InteropServices;
using Inventory.Contracts;

namespace Inventory.Agent.MacOS;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is [RunnerModes.Version])
        {
            Console.WriteLine(new MacAgentPlatform().Version);
            return 0;
        }

        // Inventar auf der Konsole: zum Prüfen auf dem Mac, ohne Server.
        if (args is [RunnerModes.Collect])
        {
            if (!MacInventoryCollector.IsMac)
                Console.Error.WriteLine("Warning: not running on macOS, most values stay empty.");
            Console.WriteLine(JobJson.Serialize(new MacInventoryCollector(MacAgentPlatform.LocalHostName()).Collect()));
            return 0;
        }

        if (args is [RunnerModes.Agent] or [RunnerModes.AgentConsole])
        {
            var loop = new AgentLoop(new MacAgentPlatform());
            // launchd beendet den Dienst mit SIGTERM.
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; loop.Stop(); });
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; loop.Stop(); };
            loop.Run();
            return 0;
        }

        Console.Error.WriteLine("Usage: inventory-agent --agent | --collect | --version");
        return 2;
    }
}
