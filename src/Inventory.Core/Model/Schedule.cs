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
    /// <summary>Monatlich an einem Tag oder am n-ten Wochentag, wahlweise um Tage versetzt (z. B. drei Tage nach dem zweiten Dienstag).</summary>
    Monthly = 4,
    /// <summary>
    /// Pflicht-Verteilung: das Paket läuft auf jedem Mitglied der Sammlung, auf dem es über diesen Zeitplan noch nicht
    /// erfolgreich war; neue Mitglieder kommen von selbst dazu. Nur für <see cref="ScheduleAction.Deploy"/>.
    /// </summary>
    OnMembership = 5,
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
    /// <summary>Bei Deployments: Rechner außerhalb ihres Wartungsfensters warten, bis es beginnt.</summary>
    public bool RespectMaintenanceWindow { get; set; } = true;
    /// <summary>Wie <see cref="Deployment.MaxFailures"/>.</summary>
    public int? MaxFailures { get; set; }
    /// <summary>Wie <see cref="Deployment.MaxFailurePercent"/>; neue Zeitpläne halten ab 10 % Fehlern an, damit ein kaputtes Paket nachts nicht alle Rechner erreicht.</summary>
    public int? MaxFailurePercent { get; set; } = 10;
    /// <summary>Wie <see cref="Deployment.MaxParallel"/>.</summary>
    public int? MaxParallel { get; set; }
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
    /// <summary>Monthly: Tag des Monats (1–31, in kürzeren Monaten der letzte); 0 = stattdessen <see cref="MonthWeek"/> und <see cref="MonthWeekday"/>.</summary>
    public int MonthDay { get; set; } = 1;
    /// <summary>Monthly: der wievielte Wochentag des Monats (1–4, 5 = der letzte).</summary>
    public int MonthWeek { get; set; } = 2;
    /// <summary>Monthly: Wochentag zu <see cref="MonthWeek"/> (Sonntag = 0).</summary>
    public int MonthWeekday { get; set; } = (int)DayOfWeek.Tuesday;
    /// <summary>Monthly: so viele Tage nach dem berechneten Tag.</summary>
    public int MonthOffsetDays { get; set; }
    /// <summary>Nach diesem Zeitpunkt (Ortszeit des Servers) läuft der Zeitplan nicht mehr; null = unbegrenzt.</summary>
    public DateTime? EndLocal { get; set; }
    /// <summary>
    /// War der Server zum geplanten Zeitpunkt aus: true = den Lauf nachholen, wenn er weniger als einen Tag zurückliegt;
    /// false = auslassen und zum nächsten regulären Termin laufen. Gilt für Daily, Weekly und Monthly.
    /// </summary>
    public bool CatchUpMissed { get; set; } = true;
    /// <summary>OnMembership: nach so vielen Tagen erneut ausführen, auch wenn es schon erfolgreich war (die Erkennung des Pakets stellt dann fest, ob es noch da ist); 0 = nie.</summary>
    public int RecheckDays { get; set; }

    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public string? LastResult { get; set; }
}
