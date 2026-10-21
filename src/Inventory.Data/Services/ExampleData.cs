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

    /// <summary>
    /// Beispiel-Berichte, einmalig; liefert die Zahl der angelegten. Anders als bei den Zeitplänen auch neben vorhandenen
    /// Berichten: sie laufen nicht von selbst, und wer schon einen eigenen angelegt hat, soll die Vorlagen trotzdem bekommen.
    /// </summary>
    public async Task<int> SeedReportsAsync(CancellationToken ct = default) =>
        await SeedFirstReportsAsync(ct) + await SeedDecommissionedReportAsync(ct);

    private async Task<int> SeedFirstReportsAsync(CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.ExampleReportsCreated, ct) == bool.TrueString)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = (await db.Reports.Select(r => r.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = ExampleReports(L).Where(r => !existing.Contains(r.Name)).ToList();
        db.Reports.AddRange(added);
        await db.SaveChangesAsync(ct);
        // Erst nach dem Speichern als erledigt vermerken: bricht der Start vorher ab, holt der nächste es nach
        // (vorhandene Namen werden übersprungen, doppelt entsteht nichts).
        await settings.SetAsync(SettingKeys.ExampleReportsCreated, bool.TrueString, ct);
        return added.Count;
    }

    /// <summary>
    /// Später hinzugekommen und darum mit eigenem Vermerk, damit auch bestehende Installationen ihn einmal bekommen:
    /// Rechner, die vermutlich außer Betrieb sind oder im AD fehlen. Berichte filtern nur über Sammlungen, darum entsteht
    /// dazu eine dynamische Sammlung; eine gleichnamige vorhandene wird verwendet.
    /// </summary>
    private async Task<int> SeedDecommissionedReportAsync(CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.ExampleDecommissionedReportCreated, ct) == bool.TrueString)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var report = DecommissionedReport(L);
        var added = 0;
        if (!await db.Reports.AnyAsync(r => r.Name == report.Name, ct))
        {
            var collection = DecommissionedCollection(L);
            report.Collection = await db.Collections.FirstOrDefaultAsync(c => c.Name == collection.Name, ct) ?? collection;
            db.Reports.Add(report);
            await db.SaveChangesAsync(ct);
            added = 1;
        }
        await settings.SetAsync(SettingKeys.ExampleDecommissionedReportCreated, bool.TrueString, ct);
        return added;
    }

    /// <summary>Vermutlich außer Betrieb (keine Anmeldung, keine Sichtung) oder nicht (mehr) im AD.</summary>
    public static Collection DecommissionedCollection(IStringLocalizer<SharedResource> L) => new()
    {
        Name = L[Prefix] + L["Possibly decommissioned"],
        Description = L["Presumably decommissioned or not (anymore) in Active Directory"],
        IsDynamic = true,
        MatchAll = false,
        Rules =
        [
            new() { Field = RuleField.DirectoryInactive, Operator = RuleOperator.Equals, Value = "true" },
            new() { Field = RuleField.MissingInDirectory, Operator = RuleOperator.Equals, Value = "true" },
        ],
    };

    public static Report DecommissionedReport(IStringLocalizer<SharedResource> L) => new()
    {
        Name = L[Prefix] + L["Possibly decommissioned"],
        Description = L["Computers without AD logon or sign of life for a long time, or no longer in AD – the longest silent first"],
        Source = ReportSource.Computers,
        Columns = "name,adInactive,adLastLogon,lastSighting,adMissingSince,adDisabled,adOu,os,primaryUser,location,description",
        SortColumn = "adLastLogon",
    };

    /// <summary>Häufig gefragte Listen als Ausgangspunkt: so wie sie sind verwendbar, oder dupliziert und angepasst.</summary>
    public static List<Report> ExampleReports(IStringLocalizer<SharedResource> L) =>
    [
        new()
        {
            Name = L[Prefix] + L["Hardware overview"], Description = L["Device type, model, serial number, processor and memory of every computer"], Source = ReportSource.Computers,
            Columns = "name,deviceType,manufacturer,model,serial,cpu,memoryGb,os,osRelease,lastScan", SortColumn = "name",
        },
        new()
        {
            Name = L[Prefix] + L["Security status"], Description = L["Firewall, antivirus, drive encryption and platform security per computer"], Source = ReportSource.Computers,
            Columns = "name,firewallDomain,firewallPrivate,firewallPublic,defenderRealTime,antivirus,bitLocker,secureBoot,tpm,rebootPending,pendingSecurityUpdates", SortColumn = "name",
        },
        new()
        {
            Name = L[Prefix] + L["Patch status"], Description = L["Pending updates per computer, the ones with the most security updates first"], Source = ReportSource.Computers,
            Columns = "name,os,osRelease,lastUpdateInstall,pendingUpdates,pendingSecurityUpdates,rebootPending,lastScan", SortColumn = "pendingSecurityUpdates", SortDescending = true,
        },
        new()
        {
            Name = L[Prefix] + L["Asset list"], Description = L["Who uses which device, where it is and how long the warranty lasts"], Source = ReportSource.Computers,
            Columns = "name,primaryUser,owner,location,costCenter,inventoryNumber,model,serial,purchaseDate,warrantyUntil", SortColumn = "warrantyUntil",
        },
        new()
        {
            Name = L[Prefix] + L["Operating systems"], Description = L["Windows edition and feature update, the oldest first"], Source = ReportSource.Computers,
            Columns = "name,os,osRelease,osVersion,activated,lastBoot,lastScan", SortColumn = "osRelease",
        },
        new()
        {
            Name = L[Prefix] + L["Windows 11 requirements"], Description = L["Firmware, Secure Boot, TPM, processor and memory"], Source = ReportSource.Computers,
            Columns = "name,model,os,cpu,memoryGb,firmware,secureBoot,tpm,tpmReady", SortColumn = "name",
        },
        new()
        {
            Name = L[Prefix] + L["Installed software"], Description = L["Every program on every computer"], Source = ReportSource.Software,
            Columns = "computer,program,version,publisher,installed", SortColumn = "program",
        },
        new()
        {
            Name = L[Prefix] + L["Pending updates"], Description = L["Every pending update on every computer"], Source = ReportSource.PendingUpdates,
            Columns = "computer,kb,title,classification,severity,security,lastSearch", SortColumn = "computer",
        },
        new() { Name = L[Prefix] + L["Deployments of the last 30 days"], Source = ReportSource.Deployments, Days = 30 },
        new() { Name = L[Prefix] + L["Changes of the last 7 days"], Description = L["What was installed, removed or changed"], Source = ReportSource.Changes, Days = 7 },
    ];

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
