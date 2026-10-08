using Inventory.Core.Model;

namespace Inventory.Core;

public static class ScheduleCalculator
{
    /// <summary>Nächster Ausführungszeitpunkt (Ortszeit) nach <paramref name="afterLocal"/>; null, wenn der Zeitplan nicht mehr läuft.</summary>
    public static DateTime? Next(Schedule schedule, DateTime afterLocal)
    {
        switch (schedule.Trigger)
        {
            case ScheduleTrigger.Once:
                // Ein verpasster Einmal-Termin (Server war aus) wird nachgeholt, aber nur ein einziges Mal ausgeführt.
                return schedule.LastRunUtc is null ? schedule.StartLocal ?? afterLocal : null;

            case ScheduleTrigger.Interval:
                var interval = TimeSpan.FromMinutes(Math.Max(schedule.IntervalMinutes, 1));
                if (schedule.LastRunUtc is null)
                    return schedule.StartLocal is { } start && start > afterLocal ? start : afterLocal;
                return afterLocal + interval;

            case ScheduleTrigger.Daily:
                var today = afterLocal.Date + schedule.TimeOfDay;
                return today > afterLocal ? today : today.AddDays(1);

            case ScheduleTrigger.Weekly:
                if ((schedule.DaysOfWeek & 0x7F) == 0)
                    return null;
                for (var offset = 0; offset <= 7; offset++)
                {
                    var candidate = afterLocal.Date.AddDays(offset) + schedule.TimeOfDay;
                    if (candidate > afterLocal && (schedule.DaysOfWeek & (1 << (int)candidate.DayOfWeek)) != 0)
                        return candidate;
                }
                return null;

            default:
                return null;
        }
    }
}
