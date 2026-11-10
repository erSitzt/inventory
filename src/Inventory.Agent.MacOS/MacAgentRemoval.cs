using System.Diagnostics;
using Inventory.Contracts;

namespace Inventory.Agent.MacOS;

/// <summary>
/// Entfernt den Mac-Agenten auf Anweisung des Servers. Der Agent kann sich nicht selbst beenden und zugleich sein Ergebnis
/// melden: ein abgekoppeltes Skript wartet, bis das Ergebnis dieses Auftrags abgelegt und dann abgeliefert ist (ohne Frist:
/// sonst wüsste der Server nie davon), löscht Ordner und plist und entlädt zuletzt den LaunchDaemon – das beendet auch den
/// Agenten, und launchd startet ihn nicht wieder. Wird das Ergebnis gar nicht abgelegt, bleibt der Agent.
/// </summary>
internal static class MacAgentRemoval
{
    /// <summary>Das Skript; Agent-Ordner und Job-ID kommen als Argumente, damit nichts davon im Skripttext steht.</summary>
    public static string Script =>
        $$"""
        result="$1/{{AgentProtocol.PendingFolder}}/$2.json"
        job="$1/{{JobFiles.JobsFolder}}/$2"
        # Abgelegt ist das Ergebnis, sobald es da ist – oder der Agent das Job-Verzeichnis schon weggeräumt hat (das tut er erst danach).
        for i in $(seq 1 120); do
          { [ -f "$result" ] || [ ! -d "$job" ]; } && break
          sleep 0.5
        done
        [ -f "$result" ] || [ ! -d "$job" ] || exit 1
        while [ -f "$result" ]; do
          sleep 2
        done
        rm -f '/Library/LaunchDaemons/{{AgentProtocol.MacAgentLabel}}.plist'
        rm -rf "$1"
        launchctl bootout system/{{AgentProtocol.MacAgentLabel}}
        """;

    public static string Schedule(string agentDirectory, string jobId)
    {
        var info = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", Script, "inventory-agent-removal", agentDirectory, jobId })
            info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
        return "The agent removes itself as soon as this result has reached the server.";
    }
}
