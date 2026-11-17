using System.Globalization;
using Inventory.Contracts;
using Inventory.Core.Model;
using Microsoft.Extensions.Localization;

namespace Inventory.Core;

/// <summary>Das Wichtigste einer Update-Richtlinie in einer Zeile je Aspekt; null = nicht festgelegt (Windows-Standard).</summary>
/// <param name="Source">WSUS-Adresse, „Windows Update for Business (Intune)“ oder „Microsoft Update“.</param>
/// <param name="Automatic">Modus der automatischen Updates samt Zeitplan.</param>
/// <param name="Gpos">Die GPOs, deren Werte gelten.</param>
/// <param name="Conflicts">Eine überstimmte GPO setzt einen Wert anders.</param>
/// <param name="Deferral">Zurückgestellte Qualitäts- und Funktionsupdates, z. B. „Qualitätsupdates 7 Tage“.</param>
public sealed record UpdatePolicySummary(string? Source, string? TargetGroup, string? Automatic, IReadOnlyList<string> Gpos, bool Conflicts, string? Deferral = null);

/// <summary>
/// Liest die Werte der Windows-Update-Richtlinie (Gruppenrichtlinie unter Policies\…\WindowsUpdate, Intune unter
/// PolicyManager\…\Update) für Menschen: Bezeichnungen, Werte, Zusammenfassung, Gruppierung und Verlauf an einer Stelle.
/// </summary>
public static class UpdatePolicies
{
    /// <summary>Werte, die 1 für ja und 0 für nein bedeuten.</summary>
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "TargetGroupEnabled", "DisableDualScan", "DoNotConnectToWindowsUpdateInternetLocations", "SetDisableUXWUAccess", "DeferQualityUpdates",
        "DeferFeatureUpdates", "TargetReleaseVersion", "ExcludeWUDriversInQualityUpdate", "ExcludeWUDriversInQualityUpdates", "SetActiveHours",
        "SetComplianceDeadline", "ConfigureDeadlineNoAutoReboot", "ElevateNonAdmins", "AcceptTrustedPublisherCerts", "NoAutoUpdate", "UseWUServer",
        "DetectionFrequencyEnabled", "NoAutoRebootWithLoggedOnUsers", "AlwaysAutoRebootAtScheduledTime", "RebootRelaunchTimeoutEnabled",
        "RebootWarningTimeoutEnabled", "AutoInstallMinorUpdates", "AllowMUUpdateService", "IncludeRecommendedUpdates", "AUPowerManagement",
        "ScheduledInstallEveryWeek", "ScheduledInstallFirstWeek", "ScheduledInstallSecondWeek", "ScheduledInstallThirdWeek", "ScheduledInstallFourthWeek",
        "QualityUpdateEnrolled", "FeatureUpdateEnrolled", "DriverUpdateEnrolled", "AllowRebootlessUpdates", "PauseQualityUpdates", "PauseFeatureUpdates",
    };

    public static bool IsMdm(ComputerUpdatePolicySetting setting) => setting.Source == UpdatePolicySources.Mdm;

    /// <summary>Bezeichnung der Einstellung; unbekannte Namen bleiben roh.</summary>
    public static string Label(ComputerUpdatePolicySetting setting, IStringLocalizer L) => setting.Name switch
    {
        "WUServer" => L["Update server (WSUS)"],
        "WUStatusServer" => L["Statistics server (WSUS)"],
        "UpdateServiceUrlAlternate" => L["Alternate download server"],
        "UpdateServiceUrl" => L["Update server"],
        "TargetGroupEnabled" => L["Client-side targeting"],
        "TargetGroup" => L["WSUS target group"],
        "DisableDualScan" => L["Do not scan Windows Update when deferrals are set"],
        "DoNotConnectToWindowsUpdateInternetLocations" => L["Do not connect to Windows Update internet locations"],
        "SetDisableUXWUAccess" => L["Remove access to all Windows Update features"],
        "DeferQualityUpdates" => L["Defer quality updates"],
        "DeferQualityUpdatesPeriodInDays" => L["Quality update deferral (days)"],
        "DeferFeatureUpdates" => L["Defer feature updates"],
        "DeferFeatureUpdatesPeriodInDays" => L["Feature update deferral (days)"],
        "PauseQualityUpdates" or "PauseQualityUpdatesStartTime" => L["Pause quality updates"],
        "PauseFeatureUpdates" or "PauseFeatureUpdatesStartTime" => L["Pause feature updates"],
        "TargetReleaseVersion" => L["Pin the feature update version"],
        "TargetReleaseVersionInfo" => L["Target version"],
        "ProductVersion" => L["Target product"],
        "ExcludeWUDriversInQualityUpdate" or "ExcludeWUDriversInQualityUpdates" => L["Exclude drivers from quality updates"],
        "SetActiveHours" => L["Active hours"],
        "ActiveHoursStart" => L["Active hours start"],
        "ActiveHoursEnd" => L["Active hours end"],
        "SetComplianceDeadline" => L["Deadlines"],
        "ConfigureDeadlineForQualityUpdates" => L["Deadline for quality updates (days)"],
        "ConfigureDeadlineForFeatureUpdates" => L["Deadline for feature updates (days)"],
        "ConfigureDeadlineGracePeriod" => L["Grace period before the restart (days)"],
        "ConfigureDeadlineNoAutoReboot" => L["No automatic restart before the deadline"],
        "SetPolicyDrivenUpdateSourceForQualityUpdates" => L["Source of quality updates"],
        "SetPolicyDrivenUpdateSourceForFeatureUpdates" => L["Source of feature updates"],
        "SetPolicyDrivenUpdateSourceForDriverUpdates" => L["Source of driver updates"],
        "SetPolicyDrivenUpdateSourceForOtherUpdates" => L["Source of other updates"],
        "BranchReadinessLevel" => L["Release channel"],
        "ElevateNonAdmins" => L["Users without administrator rights get update notifications"],
        "AcceptTrustedPublisherCerts" => L["Signed updates from an intranet service"],
        "NoAutoUpdate" => L["Automatic updates turned off"],
        "AUOptions" or "AllowAutoUpdate" => L["Automatic updates"],
        "ScheduledInstallDay" => L["Scheduled install day"],
        "ScheduledInstallTime" => L["Scheduled install time"],
        "ScheduledInstallEveryWeek" => L["Install every week"],
        "ScheduledInstallFirstWeek" => L["Install in the first week of the month"],
        "ScheduledInstallSecondWeek" => L["Install in the second week of the month"],
        "ScheduledInstallThirdWeek" => L["Install in the third week of the month"],
        "ScheduledInstallFourthWeek" => L["Install in the fourth week of the month"],
        "UseWUServer" => L["Use the WSUS server"],
        "DetectionFrequencyEnabled" => L["Own detection frequency"],
        "DetectionFrequency" => L["Detection frequency (hours)"],
        "NoAutoRebootWithLoggedOnUsers" => L["No automatic restart with signed-in users"],
        "AlwaysAutoRebootAtScheduledTime" => L["Always restart at the scheduled time"],
        "AlwaysAutoRebootAtScheduledTimeMinutes" => L["Warning before the scheduled restart (minutes)"],
        "RebootRelaunchTimeoutEnabled" or "RebootRelaunchTimeout" => L["Ask again for the restart after (minutes)"],
        "RebootWarningTimeoutEnabled" or "RebootWarningTimeout" => L["Warning before the automatic restart (minutes)"],
        "AutoInstallMinorUpdates" => L["Install minor updates right away"],
        "AllowMUUpdateService" => L["Updates for other Microsoft products"],
        "IncludeRecommendedUpdates" => L["Recommended updates"],
        "AUPowerManagement" => L["Wake the computer for the installation"],
        "QualityUpdateEnrolled" => L["Quality updates managed by the deployment service"],
        "FeatureUpdateEnrolled" => L["Feature updates managed by the deployment service"],
        "DriverUpdateEnrolled" => L["Driver updates managed by the deployment service"],
        "AllowRebootlessUpdates" => L["Hotpatch (updates without a restart)"],
        _ => setting.Name,
    };

    /// <summary>Der Wert für Menschen: ja/nein, Modus, Wochentag, Uhrzeit; sonst wie gelesen.</summary>
    public static string? Display(ComputerUpdatePolicySetting setting, IStringLocalizer L) => Display(setting.Name, setting.Value, IsMdm(setting), L);

    public static string? Display(string name, string? value, bool mdm, IStringLocalizer L)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return value;
        if (Switches.Contains(name) && number is 0 or 1)
            return number == 1 ? L["yes"] : L["no"];
        return name switch
        {
            "AUOptions" => AuOption(number, L) ?? value,
            "AllowAutoUpdate" => MdmAutoUpdate(number, L) ?? value,
            "ScheduledInstallDay" => Day(number, L) ?? value,
            "ScheduledInstallTime" or "ActiveHoursStart" or "ActiveHoursEnd" when number is >= 0 and <= 24 => $"{number % 24:00}:00",
            "SetPolicyDrivenUpdateSourceForQualityUpdates" or "SetPolicyDrivenUpdateSourceForFeatureUpdates" or "SetPolicyDrivenUpdateSourceForDriverUpdates"
                or "SetPolicyDrivenUpdateSourceForOtherUpdates" => number == 1 ? "WSUS" : "Windows Update",
            _ => value,
        };
    }

    /// <summary>AUOptions der Gruppenrichtlinie „Automatische Updates konfigurieren“.</summary>
    private static string? AuOption(int value, IStringLocalizer L) => value switch
    {
        2 => L["Notify before download"],
        3 => L["Download automatically, notify before install"],
        4 => L["Download and install on a schedule"],
        5 => L["Local administrator chooses"],
        7 => L["Download automatically, notify before install and restart"],
        _ => null,
    };

    /// <summary>Update/AllowAutoUpdate der Intune-Richtlinie (Policy CSP).</summary>
    private static string? MdmAutoUpdate(int value, IStringLocalizer L) => value switch
    {
        0 => L["Notify before download"],
        1 => L["Install automatically, notify before restart"],
        2 => L["Install and restart at the scheduled time"],
        3 => L["Install and restart at the scheduled time without user control"],
        4 => L["Install and restart automatically without user control"],
        5 => L["Automatic updates turned off"],
        6 => L["Install and restart at the scheduled time"],
        _ => null,
    };

    /// <summary>0 = täglich, 1 = Sonntag bis 7 = Samstag; Wochentage in der Sprache der Oberfläche.</summary>
    private static string? Day(int value, IStringLocalizer L) => value switch
    {
        0 => L["Every day"],
        >= 1 and <= 7 => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)(value - 1)),
        _ => null,
    };

    public static UpdatePolicySummary Summarize(IReadOnlyCollection<ComputerUpdatePolicySetting> settings, IStringLocalizer L)
    {
        var policy = settings.Where(s => !IsMdm(s)).ToList();
        var mdm = settings.Where(IsMdm).ToList();
        string? Get(IEnumerable<ComputerUpdatePolicySetting> list, string name) =>
            list.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        bool On(IEnumerable<ComputerUpdatePolicySetting> list, string name) => Get(list, name) == "1";

        string? source = null;
        if (On(policy, "UseWUServer") && Get(policy, "WUServer") is { Length: > 0 } wsus)
            source = wsus;
        else if (mdm.Count > 0)
            source = L["Windows Update for Business (Intune)"];
        else if (settings.Count > 0)
            source = "Microsoft Update";

        var targetGroup = On(policy, "TargetGroupEnabled") ? Get(policy, "TargetGroup") : null;

        // Gruppenrichtlinie vor Intune: Ohne MDMWinsOverGP gewinnt bei Windows die Gruppenrichtlinie.
        string? automatic = null;
        if (On(policy, "NoAutoUpdate"))
            automatic = L["off"];
        else if (Get(policy, "AUOptions") is { } options)
            automatic = WithSchedule(Display("AUOptions", options, false, L), options == "4", policy, L);
        else if (Get(mdm, "AllowAutoUpdate") is { } allow)
            automatic = WithSchedule(Display("AllowAutoUpdate", allow, true, L), allow is "2" or "3" or "6", mdm, L);

        var conflicts = settings.Any(s => s.Overrides.Any(o => !string.Equals(o.Value, s.Value, StringComparison.OrdinalIgnoreCase)));

        var deferrals = new List<string>();
        if (Days("DeferQualityUpdatesPeriodInDays", "DeferQualityUpdates") is { } quality)
            deferrals.Add(L["quality updates {0} days", quality]);
        if (Days("DeferFeatureUpdatesPeriodInDays", "DeferFeatureUpdates") is { } feature)
            deferrals.Add(L["feature updates {0} days", feature]);
        var deferral = deferrals.Count == 0 ? null : L["Deferred: {0}", string.Join(", ", deferrals)];

        return new UpdatePolicySummary(source, string.IsNullOrWhiteSpace(targetGroup) ? null : targetGroup, automatic, Gpos(settings), conflicts, deferral);

        // Gruppenrichtlinie: Die Tage gelten nur mit eingeschalteter Zurückstellung (DeferQualityUpdates usw.); Intune kennt
        // keinen Schalter, dort zählen die Tage allein.
        int? Days(string name, string switchName)
        {
            var text = Get(policy, name) is { } fromPolicy ? (On(policy, switchName) ? fromPolicy : null) : Get(mdm, name);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : null;
        }

        string? WithSchedule(string? mode, bool scheduled, List<ComputerUpdatePolicySetting> list, IStringLocalizer l)
        {
            if (!scheduled)
                return mode;
            var day = Get(list, "ScheduledInstallDay") is { } d ? Display("ScheduledInstallDay", d, false, l) : null;
            var time = Get(list, "ScheduledInstallTime") is { } t ? Display("ScheduledInstallTime", t, false, l) : null;
            var when = string.Join(" ", new[] { day, time }.Where(x => x is not null));
            return when.Length == 0 ? mode : $"{mode}: {when}";
        }
    }

    /// <summary>Die GPOs, deren Werte gelten, alphabetisch.</summary>
    public static IReadOnlyList<string> Gpos(IEnumerable<ComputerUpdatePolicySetting> settings) =>
        settings.Select(s => s.Gpo).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>WSUS-Zielgruppe, wenn die clientseitige Zuordnung eingeschaltet ist.</summary>
    public static string? TargetGroup(IEnumerable<ComputerUpdatePolicySetting> settings)
    {
        var list = settings.Where(s => !IsMdm(s)).ToList();
        return list.Any(s => s.Name.Equals("TargetGroupEnabled", StringComparison.OrdinalIgnoreCase) && s.Value == "1")
            ? list.FirstOrDefault(s => s.Name.Equals("TargetGroup", StringComparison.OrdinalIgnoreCase))?.Value
            : null;
    }

    /// <summary>Gleiche Richtlinie = gleiche Werte aus denselben Quellen; leer = keine Richtlinie.</summary>
    public static string Fingerprint(IEnumerable<ComputerUpdatePolicySetting> settings) =>
        string.Join("\n", settings.Select(s => $"{s.Key}|{s.Name}={s.Value}@{s.Gpo ?? s.Source}").Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>Einträge im Verlauf: der Name als Schlüssel, der Wert mit seiner GPO; so erscheint ein geänderter Wert als Änderung.</summary>
    public static IEnumerable<(string Key, string? Value)> HistoryItems(IEnumerable<ComputerUpdatePolicySetting> settings) =>
        settings.Select(s => (IsMdm(s) ? $"Intune: {s.Name}" : s.Name, (string?)(s.Gpo is { } gpo ? $"{s.Value} ({gpo})" : s.Value)));

    public static ComputerUpdatePolicySetting ToEntity(UpdatePolicySetting setting, int computerId) => new()
    {
        ComputerId = computerId, Source = setting.Source, Key = setting.Key, Name = setting.Name, Value = setting.Value, Gpo = setting.Gpo,
        AlsoSetBy = Overrides(setting.AlsoSetBy),
    };

    /// <summary>
    /// Waren die RSoP-Daten diesmal nicht lesbar, behalten unveränderte Werte ihre bisherige Herkunft: Sonst stünde jeder Wert im
    /// Verlauf als geändert, nur weil seine GPO für einen Scan unbekannt war.
    /// </summary>
    public static void CarryOrigin(IEnumerable<ComputerUpdatePolicySetting> previous, UpdatePolicyInfo current)
    {
        if (current.RsopRead)
            return;
        var known = previous.Where(p => p.Gpo is not null).GroupBy(p => (p.Key, p.Name, p.Value))
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var setting in current.Settings.Where(s => s.Source == UpdatePolicySources.Registry))
        {
            if (!known.TryGetValue((setting.Key, setting.Name, setting.Value), out var before))
                continue;
            setting.Source = before.Source;
            setting.Gpo = before.Gpo;
            setting.AlsoSetBy = before.Overrides.Select(o => new UpdatePolicyOverride { Gpo = o.Gpo, Value = o.Value }).ToList();
        }
    }

    /// <summary>Überstimmte GPOs zeilenweise für <see cref="ComputerUpdatePolicySetting.AlsoSetBy"/>.</summary>
    public static string? Overrides(IEnumerable<UpdatePolicyOverride> overrides)
    {
        var lines = overrides.Where(o => !string.IsNullOrWhiteSpace(o.Gpo)).Select(o => $"{o.Gpo.Replace('\t', ' ').Replace('\n', ' ')}\t{o.Value?.Replace('\n', ' ')}").ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }
}
