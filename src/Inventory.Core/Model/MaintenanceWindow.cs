namespace Inventory.Core.Model;

/// <summary>Zeitraum, in dem Deployments auf einem Rechner laufen dürfen: Wochentage und Uhrzeit in Ortszeit des Servers.</summary>
public sealed class MaintenanceWindow
{
    /// <summary>Bitmaske, Bit n = <see cref="DayOfWeek"/> n (Sonntag = 0); bei Fenstern über Mitternacht zählt der Tag des Beginns.</summary>
    public int DaysOfWeek { get; set; }
    public TimeSpan From { get; set; }
    /// <summary>Liegt vor <see cref="From"/>, reicht das Fenster über Mitternacht; gleich <see cref="From"/> = den ganzen Tag.</summary>
    public TimeSpan To { get; set; }

    public bool Contains(DateTime local)
    {
        var time = local.TimeOfDay;
        if (From == To)
            return Has(local.DayOfWeek);
        if (From < To)
            return Has(local.DayOfWeek) && time >= From && time < To;
        return (Has(local.DayOfWeek) && time >= From) || (Has(local.AddDays(-1).DayOfWeek) && time < To);
    }

    /// <summary>Nächster Beginn nach <paramref name="local"/>; null, wenn kein Wochentag gewählt ist.</summary>
    public DateTime? NextStart(DateTime local)
    {
        for (var offset = 0; offset <= 7; offset++)
        {
            var candidate = local.Date.AddDays(offset) + From;
            if (candidate > local && Has(candidate.DayOfWeek))
                return candidate;
        }
        return null;
    }

    private bool Has(DayOfWeek day) => (DaysOfWeek & (1 << (int)day)) != 0;
}
