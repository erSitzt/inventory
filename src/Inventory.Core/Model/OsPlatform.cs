namespace Inventory.Core.Model;

/// <summary>Betriebssystem-Familie eines Rechners; bestimmt, welche Daten, Ansichten und Aktionen es für ihn gibt.</summary>
public enum OsPlatform
{
    Windows = 0,
    /// <summary>Mac mit Agent; bisher nur Inventar, keine Befehle und Pakete.</summary>
    MacOS = 1,
}
