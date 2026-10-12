namespace Inventory.Core.Model;

/// <summary>Weg, über den ein Job auf den Zielrechner gelangt.</summary>
public enum TransportKind
{
    /// <summary>Runner über ADMIN$ kopieren und als temporären Dienst starten.</summary>
    SmbService = 0,
    /// <summary>PowerShell-Remoting.</summary>
    WinRm = 1,
    /// <summary>Fest installierter Agent.</summary>
    Agent = 2,
}

/// <summary>Legt fest, wann der Server ohne Agenten auf Rechner zugreifen darf (ADMIN$ + Dienst oder WinRM).</summary>
public enum AgentlessPolicy
{
    /// <summary>Jeder Job darf agentenlos laufen.</summary>
    Always = 0,
    /// <summary>Nur die Installation des Agenten läuft agentenlos; alles andere braucht den Agenten.</summary>
    AgentInstallOnly = 1,
    /// <summary>Kein agentenloser Zugriff; der Agent kommt auf anderem Weg auf die Rechner.</summary>
    Never = 2,
}
