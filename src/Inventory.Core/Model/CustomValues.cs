namespace Inventory.Core.Model;

/// <summary>Wert eines eigenen Inventarfelds (aus einem Plugin) für einen Rechner; normalisiert nach dem Feldtyp gespeichert.</summary>
public class ComputerCustomValue
{
    public long Id { get; set; }
    public int ComputerId { get; set; }
    /// <summary>Vollständiger Schlüssel des Felds, "&lt;Plugin-Id&gt;.&lt;Name&gt;".</summary>
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Letzter Lauf eines Inventarskripts auf einem Rechner: wann, und welcher Fehler, falls einer auftrat.</summary>
public class ComputerCustomScriptRun
{
    public long Id { get; set; }
    public int ComputerId { get; set; }
    /// <summary>"&lt;Plugin-Id&gt;/&lt;Skriptname&gt;".</summary>
    public required string Script { get; set; }
    public DateTime RunUtc { get; set; }
    public string? Error { get; set; }
}
