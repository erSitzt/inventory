namespace Inventory.Core.Model;

public enum ScheduleAction
{
    Scan,
    Deploy,
}

public enum ScheduleTrigger
{
    Once,
    Interval,
    Daily,
    Weekly,
}

/// <summary>Führt regelmäßig einen Scan oder ein Paket auf einer Sammlung (oder allen Rechnern) aus.</summary>
public class Schedule
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public ScheduleAction Action { get; set; }
    public int? PackageId { get; set; }
    public Package? Package { get; set; }
    public bool ScanAfter { get; set; } = true;
    /// <summary>Bei Deployments: Offline-Rechner nachholen, sobald sie erreichbar sind.</summary>
    public bool RetryOffline { get; set; } = true;
    /// <summary>null = alle Rechner.</summary>
    public int? CollectionId { get; set; }
    public Collection? Collection { get; set; }

    public ScheduleTrigger Trigger { get; set; }
    /// <summary>Once: Ausführungszeitpunkt. Interval: frühester Beginn. Ortszeit des Servers.</summary>
    public DateTime? StartLocal { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Daily/Weekly: Uhrzeit in Ortszeit des Servers.</summary>
    public TimeSpan TimeOfDay { get; set; } = new(12, 0, 0);
    /// <summary>Weekly: Bitmaske, Bit n = <see cref="DayOfWeek"/> n (Sonntag = 0).</summary>
    public int DaysOfWeek { get; set; }

    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public string? LastResult { get; set; }
}
