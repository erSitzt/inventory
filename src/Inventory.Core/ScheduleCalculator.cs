using Inventory.Core.Model;

namespace Inventory.Core;

public static class ScheduleCalculator
{
    /// <summary>Nächster Ausführungszeitpunkt (Ortszeit) nach <paramref name="afterLocal"/>; null, wenn der Zeitplan nicht mehr läuft.</summary>
    public static DateTime? Next(Schedule schedule, DateTime afterLocal) =>
        Unlimited(schedule, afterLocal) is { } next && (schedule.EndLocal is not { } end || next <= end) ? next : null;

    /// <summary>So oft prüft eine Pflicht-Verteilung, ob Mitglieder fehlen.</summary>
    public const int MembershipCheckMinutes = 15;

    /// <summary>Tag eines Monats, den ein monatlicher Zeitplan meint, ohne Versatz.</summary>
    public static DateTime MonthlyBase(Schedule schedule, int year, int month)
    {
        var days = DateTime.DaysInMonth(year, month);
        if (schedule.MonthDay > 0)
            return new DateTime(year, month, Math.Min(schedule.MonthDay, days));
        var weekday = (DayOfWeek)(((schedule.MonthWeekday % 7) + 7) % 7);
        var first = new DateTime(year, month, 1);
        var firstMatch = first.AddDays(((int)weekday - (int)first.DayOfWeek + 7) % 7);
        var candidate = firstMatch.AddDays(7 * (Math.Clamp(schedule.MonthWeek, 1, 5) - 1));
        // "Der fünfte" meint den letzten: gibt es ihn in diesem Monat nicht, ist es der vierte.
        return candidate.Month == month ? candidate : candidate.AddDays(-7);
    }

    private static DateTime? Unlimited(Schedule schedule, DateTime afterLocal)
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

            case ScheduleTrigger.Monthly:
                // Beim Vormonat beginnen: mit Versatz kann dessen Termin noch in diesem Monat liegen.
                var month = new DateTime(afterLocal.Year, afterLocal.Month, 1).AddMonths(-1);
                for (var i = 0; i < 14; i++, month = month.AddMonths(1))
                {
                    var candidate = MonthlyBase(schedule, month.Year, month.Month).AddDays(Math.Clamp(schedule.MonthOffsetDays, 0, 27)) + schedule.TimeOfDay;
                    if (candidate > afterLocal)
                        return candidate;
                }
                return null;

            case ScheduleTrigger.OnMembership:
                return schedule.LastRunUtc is null ? afterLocal : afterLocal.AddMinutes(MembershipCheckMinutes);

            default:
                return null;
        }
    }
}
