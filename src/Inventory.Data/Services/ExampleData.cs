using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Legt beim ersten Start Beispiele an, die zeigen, was möglich ist – deaktiviert, damit nichts von selbst läuft.</summary>
public sealed class ExampleData(IDbContextFactory<InventoryDbContext> dbFactory, SettingsService settings, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    public const string Prefix = "Example: ";

    /// <summary>Beispiel-Zeitpläne, einmalig und nur in eine leere Liste; liefert die Zahl der angelegten.</summary>
    public async Task<int> SeedSchedulesAsync(CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingKeys.ExampleSchedulesCreated, ct) == bool.TrueString)
            return 0;
        await settings.SetAsync(SettingKeys.ExampleSchedulesCreated, bool.TrueString, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Schedules.AnyAsync(ct))
            return 0;
        db.Schedules.AddRange(ExampleSchedules(L));
        await db.SaveChangesAsync(ct);
        return db.Schedules.Local.Count;
    }

    /// <summary>Nur Scans: ein Paket-Zeitplan braucht ein Paket, das es beim ersten Start noch nicht gibt.</summary>
    public static List<Schedule> ExampleSchedules(IStringLocalizer<SharedResource> L) =>
    [
        new()
        {
            Name = L[Prefix] + L["Daily inventory scan"], Enabled = false, Action = ScheduleAction.Scan,
            Trigger = ScheduleTrigger.Daily, TimeOfDay = new TimeSpan(6, 0, 0),
        },
        new()
        {
            Name = L[Prefix] + L["Scan every 4 hours"], Enabled = false, Action = ScheduleAction.Scan,
            Trigger = ScheduleTrigger.Interval, IntervalMinutes = 240,
        },
        new()
        {
            Name = L[Prefix] + L["Scan on weekdays after hours"], Enabled = false, Action = ScheduleAction.Scan,
            Trigger = ScheduleTrigger.Weekly, TimeOfDay = new TimeSpan(18, 30, 0),
            DaysOfWeek = Days(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday),
        },
        new()
        {
            Name = L[Prefix] + L["Scan on weekends"], Enabled = false, Action = ScheduleAction.Scan,
            Trigger = ScheduleTrigger.Weekly, TimeOfDay = new TimeSpan(3, 0, 0),
            DaysOfWeek = Days(DayOfWeek.Saturday, DayOfWeek.Sunday),
        },
    ];

    private static int Days(params DayOfWeek[] days) => days.Aggregate(0, (mask, day) => mask | (1 << (int)day));
}
