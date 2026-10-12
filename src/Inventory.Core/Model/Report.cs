namespace Inventory.Core.Model;

/// <summary>Woraus ein Bericht seine Zeilen bezieht. Die Zahlen stehen in der Datenbank: nur anhängen.</summary>
public enum ReportSource
{
    /// <summary>Eine Zeile je Rechner.</summary>
    Computers = 0,
    /// <summary>Eine Zeile je Rechner und installiertem Programm.</summary>
    Software = 1,
    /// <summary>Eine Zeile je Rechner und ausstehendem Update.</summary>
    PendingUpdates = 2,
    /// <summary>Eine Zeile je Deployment.</summary>
    Deployments = 3,
    /// <summary>Eine Zeile je Änderung am Inventar.</summary>
    Changes = 4,
}

/// <summary>Dateiformat eines Berichts beim Herunterladen oder als Anhang.</summary>
public enum ReportFormat
{
    Excel = 0,
    Pdf = 1,
    Csv = 2,
}

/// <summary>Ein gespeicherter Bericht: Quelle, Filter, Spaltenauswahl und Sortierung.</summary>
public class Report
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public ReportSource Source { get; set; }
    /// <summary>Nur Rechner dieser Sammlung; null = alle. Gilt nicht für <see cref="ReportSource.Deployments"/>.</summary>
    public int? CollectionId { get; set; }
    public Collection? Collection { get; set; }
    /// <summary>Für Deployments und Änderungen: nur die letzten so vielen Tage; null = der Standard der Quelle.</summary>
    public int? Days { get; set; }
    /// <summary>Schlüssel der Spalten in der gewünschten Reihenfolge, durch Komma getrennt; null = alle Spalten der Quelle.</summary>
    public string? Columns { get; set; }
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> ColumnKeys => string.IsNullOrWhiteSpace(Columns) ? [] : Columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool HasCollectionFilter(ReportSource source) => source != ReportSource.Deployments;
    public static bool HasPeriod(ReportSource source) => source is ReportSource.Deployments or ReportSource.Changes;
}
