namespace Inventory.Core.Model;

/// <summary>Was ein angemeldeter Benutzer oder ein API-Token darf; jede Stufe schließt die darunter ein.</summary>
public enum AccessRole
{
    None = 0,
    /// <summary>Alles ansehen, nichts verändern oder ausführen.</summary>
    Viewer = 1,
    /// <summary>Zusätzlich scannen, vorhandene Pakete verteilen, Deployments abbrechen und wiederholen, Fernzugriff.</summary>
    Operator = 2,
    /// <summary>Alles: Pakete, Sammlungen, Zeitpläne, Zugangsdaten, Einstellungen, Befehle auf Zielrechnern.</summary>
    Admin = 3,
}
