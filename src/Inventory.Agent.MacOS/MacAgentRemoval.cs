using System.Diagnostics;
using Inventory.Contracts;

namespace Inventory.Agent.MacOS;

/// <summary>
/// Entfernt den Mac-Agenten auf Anweisung des Servers. Der Agent kann sich nicht selbst beenden und zugleich sein Ergebnis
/// melden: ein abgekoppeltes Skript wartet, bis das Ergebnis abgeliefert ist, löscht Ordner und plist und entlädt zuletzt den
/// LaunchDaemon – das beendet auch den Agenten, und launchd startet ihn nicht wieder.
/// </summary>
internal static class MacAgentRemoval
{
    /// <summary>Das Skript; der Agent-Ordner kommt als erstes Argument, damit nichts davon im Skripttext steht.</summary>
    public static string Script =>
        $"""
        sleep 5
        for i in $(seq 1 60); do
          ls "$1/{AgentProtocol.PendingFolder}/"*.json >/dev/null 2>&1 || break
          sleep 2
        done
        rm -f '/Library/LaunchDaemons/{AgentProtocol.MacAgentLabel}.plist'
        rm -rf "$1"
        launchctl bootout system/{AgentProtocol.MacAgentLabel}
        """;

    public static string Schedule(string agentDirectory)
    {
        var info = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", Script, "inventory-agent-removal", agentDirectory })
            info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
        return "The agent removes itself as soon as this result has reached the server.";
    }
}
