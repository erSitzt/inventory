namespace Inventory.Plugins;

public enum CustomFieldType
{
    Text,
    Number,
    /// <summary>Gespeichert als yyyy-MM-dd.</summary>
    Date,
    /// <summary>Gespeichert als true/false.</summary>
    Bool,
}

/// <summary>
/// Eigenes Inventarfeld je Rechner: erscheint auf der Rechnerseite, im Verlauf, in Sammlungsregeln, Berichten, API und MCP.
/// Werte kommen aus einem <see cref="InventoryScript"/> oder über <see cref="ICustomFieldStore"/> (z. B. aus einem anderen System).
/// </summary>
/// <param name="Key">"&lt;Plugin-Id&gt;.&lt;Name&gt;", z. B. "contoso.lob.version"; unveränderlich, sonst gehen gespeicherte Werte verloren.</param>
public sealed record CustomField(string Key, string Title, CustomFieldType Type = CustomFieldType.Text);

/// <summary>
/// PowerShell-Skript, das nach jedem erfolgreichen Scan auf dem Rechner läuft (als System, über denselben Weg wie der Scan)
/// und genau ein JSON-Objekt ausgibt, z. B. <c>@{ version = '1.2' } | ConvertTo-Json -Compress</c>. Eigenschaften nennen
/// den Schlüssel eines eigenen Felds, mit oder ohne "&lt;Plugin-Id&gt;."; null entfernt den Wert.
/// </summary>
/// <remarks>Für den Simulationsmodus gibt eine Zeile <c># simulate: {"version":"1.2"}</c> im Skript die Antwort vor.</remarks>
public sealed record InventoryScript(string Name, string PowerShell, int TimeoutSeconds = 120);

/// <summary>Werte eigener Felder lesen und setzen, z. B. aus einem Abgleich mit einem anderen System (vom Server bereitgestellt).</summary>
public interface ICustomFieldStore
{
    /// <summary>Setzt einen Wert (null oder leer entfernt ihn); Änderungen stehen im Verlauf des Rechners.</summary>
    /// <exception cref="ArgumentException">Unbekanntes Feld oder ein Wert, der nicht zum Typ passt.</exception>
    /// <exception cref="KeyNotFoundException">Kein Rechner mit dieser Kennung.</exception>
    Task SetAsync(int computerId, string key, string? value, CancellationToken ct = default);

    /// <summary>Alle eigenen Werte eines Rechners, Schlüssel → Wert.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(int computerId, CancellationToken ct = default);
}
