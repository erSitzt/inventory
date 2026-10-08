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
