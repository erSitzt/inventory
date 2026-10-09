using System.Globalization;
using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Inventory.Server.Components;

/// <summary>Anzeigetexte für die Oberfläche; alle Texte englisch, übersetzt über den <see cref="IStringLocalizer"/>.</summary>
public sealed class Display(IStringLocalizer<SharedResource> localizer)
{
    private string T(string text) => localizer[text];

    private string T(string text, params object[] args) => localizer[text, args];

    public string Transport(TransportKind kind) => kind switch
    {
        TransportKind.SmbService => T("SMB + service"),
        TransportKind.WinRm => "WinRM",
        TransportKind.Agent => T("Agent"),
        _ => kind.ToString(),
    };

    /// <summary>Ein Agent gilt als verbunden, solange sein letzter Kontakt nur wenige Minuten zurückliegt.</summary>
    public bool AgentConnected(Computer c) => c.AgentLastSeenUtc is { } seen && DateTime.UtcNow - seen < TimeSpan.FromMinutes(5);

    /// <summary>Datum und Uhrzeit in der Schreibweise der aktuellen Sprache.</summary>
    public string LocalTime(DateTime? utc) =>
        utc is null ? "–" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string LocalTime(DateTime local) => local.ToString("g", CultureInfo.CurrentCulture);

    public string Bytes(long? bytes)
    {
        if (bytes is null)
            return "–";
        var gb = bytes.Value / 1024d / 1024 / 1024;
        return gb >= 10 ? $"{gb:N0} GB" : $"{gb:N1} GB";
    }

    public string Os(Computer c) =>
        c.OsName is null ? "–" : $"{c.OsName.Replace("Microsoft ", "")} {c.OsRelease}".Trim();

    public string Field(RuleField field) => field switch
    {
        RuleField.ComputerName => T("Computer name"),
        RuleField.NameMismatch => T("Reported name differs"),
        RuleField.MissingInDirectory => T("Not (anymore) in Active Directory"),
        RuleField.LocalAdmin => T("Local administrator"),
        RuleField.FirewallOff => T("Firewall (partly) off"),
        RuleField.AntivirusProblem => T("No real-time antivirus"),
        RuleField.RebootRequired => T("Reboot pending"),
        RuleField.PendingUpdates => T("Pending updates (count)"),
        RuleField.DaysSinceUpdate => T("Days since last update installation"),
        RuleField.Online => T("Online"),
        RuleField.Scanned => T("Scanned"),
        RuleField.OsName => T("Operating system"),
        RuleField.OsVersion => T("Operating system version"),
        RuleField.OsRelease => T("Feature update (e.g. 24H2)"),
        RuleField.Manufacturer => T("Manufacturer"),
        RuleField.Model => T("Model"),
        RuleField.LoggedOnUser => T("Logged-on user"),
        RuleField.MemoryGb => T("Memory (GB)"),
        RuleField.Software => T("Software"),
        _ => field.ToString(),
    };

    public string Operator(RuleOperator op) => op switch
    {
        RuleOperator.Contains => T("contains"),
        RuleOperator.NotContains => T("does not contain"),
        RuleOperator.Equals => T("equals"),
        RuleOperator.NotEquals => T("does not equal"),
        RuleOperator.StartsWith => T("starts with"),
        RuleOperator.LessThan => T("less than"),
        RuleOperator.GreaterOrEqual => T("greater or equal"),
        _ => op.ToString(),
    };

    /// <summary>Operatoren, die für ein Feld sinnvoll sind.</summary>
    public IReadOnlyList<RuleOperator> OperatorsFor(RuleField field) => field switch
    {
        RuleField.Online or RuleField.Scanned or RuleField.NameMismatch or RuleField.MissingInDirectory
            or RuleField.FirewallOff or RuleField.AntivirusProblem or RuleField.RebootRequired => [RuleOperator.Equals],
        RuleField.PendingUpdates or RuleField.DaysSinceUpdate => [RuleOperator.GreaterOrEqual, RuleOperator.LessThan, RuleOperator.Equals],
        RuleField.MemoryGb => [RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals],
        RuleField.OsVersion => [RuleOperator.StartsWith, RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals, RuleOperator.Contains],
        RuleField.Software or RuleField.LocalAdmin => [RuleOperator.Contains, RuleOperator.NotContains, RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.StartsWith],
        _ => [RuleOperator.Contains, RuleOperator.NotContains, RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.StartsWith],
    };

    public string Step(StepType type) => type switch
    {
        StepType.Command => T("Command (cmd)"),
        StepType.PowerShell => "PowerShell",
        StepType.Install => T("Installation"),
        StepType.FileCopy => T("Copy files"),
        StepType.Service => T("Service"),
        StepType.Sleep => T("Wait"),
        StepType.Reboot => T("Reboot"),
        StepType.InventoryScan => T("Inventory scan"),
        _ => type.ToString(),
    };

    public string StepIcon(StepType type) => type switch
    {
        StepType.Command => Icons.Material.Filled.Terminal,
        StepType.PowerShell => Icons.Material.Filled.Code,
        StepType.Install => Icons.Material.Filled.InstallDesktop,
        StepType.FileCopy => Icons.Material.Filled.FileCopy,
        StepType.Service => Icons.Material.Filled.MiscellaneousServices,
        StepType.Sleep => Icons.Material.Filled.HourglassEmpty,
        StepType.Reboot => Icons.Material.Filled.RestartAlt,
        StepType.InventoryScan => Icons.Material.Filled.ManageSearch,
        _ => Icons.Material.Filled.PlayArrow,
    };

    /// <summary>Kurzbeschreibung dessen, was ein Schritt tut, für Listen.</summary>
    public string StepSummary(JobStep step) => step.Type switch
    {
        StepType.Install => $"{step.File} {step.Arguments}".Trim(),
        StepType.FileCopy => $"{step.File} → {step.Destination}",
        StepType.Service => $"{ServiceActionName(step.ServiceAction)}: {step.ServiceName}",
        StepType.Sleep => T("{0} seconds", step.Seconds),
        StepType.Reboot => step.Seconds > 0 ? T("after {0} seconds", step.Seconds) : T("immediately"),
        StepType.InventoryScan => "",
        _ => step.Script.Split('\n')[0].Trim(),
    };

    public string RunAs(StepRunAs runAs) => runAs switch
    {
        StepRunAs.System => T("Local System"),
        StepRunAs.LoggedOnUser => T("Logged-on user"),
        _ => runAs.ToString(),
    };

    /// <summary>Schritt-Typen, bei denen sich der Ausführungskontext wählen lässt.</summary>
    public bool SupportsRunAs(StepType type) => type is StepType.Command or StepType.PowerShell or StepType.Install;

    public string ServiceActionName(ServiceAction action) => action switch
    {
        ServiceAction.Start => T("Start"),
        ServiceAction.Stop => T("Stop"),
        ServiceAction.Restart => T("Restart"),
        _ => action.ToString(),
    };

    public string Status(TargetStatus status) => status switch
    {
        TargetStatus.Queued => T("queued"),
        TargetStatus.Running => T("running"),
        TargetStatus.Succeeded => T("succeeded"),
        TargetStatus.Failed => T("failed"),
        TargetStatus.Cancelled => T("cancelled"),
        TargetStatus.WaitingOffline => T("waiting for computer"),
        _ => status.ToString(),
    };

    public Color StatusColor(TargetStatus status) => status switch
    {
        TargetStatus.Running => Color.Info,
        TargetStatus.Succeeded => Color.Success,
        TargetStatus.Failed => Color.Error,
        TargetStatus.Cancelled => Color.Warning,
        TargetStatus.WaitingOffline => Color.Warning,
        _ => Color.Default,
    };

    public string Status(StepStatus status) => status switch
    {
        StepStatus.Succeeded => T("succeeded"),
        StepStatus.Failed => T("failed"),
        StepStatus.Skipped => T("skipped"),
        _ => status.ToString(),
    };

    public Color StatusColor(StepStatus status) => status switch
    {
        StepStatus.Succeeded => Color.Success,
        StepStatus.Failed => Color.Error,
        _ => Color.Default,
    };

    /// <summary>Felder, die nur "ja" oder "nein" kennen.</summary>
    public bool IsFlag(RuleField field) => field is RuleField.Online or RuleField.Scanned or RuleField.NameMismatch or RuleField.MissingInDirectory
        or RuleField.FirewallOff or RuleField.AntivirusProblem or RuleField.RebootRequired;

    public string OnOff(bool? value) => value switch { true => T("on"), false => T("off"), null => T("unknown") };

    /// <summary>Betriebsart von Defender, wie MSFT_MpComputerStatus.AMRunningMode sie meldet.</summary>
    public string DefenderMode(string mode) => mode switch
    {
        "Normal" => T("normal"),
        "Passive Mode" => T("passive – another antivirus is active"),
        "SxS Passive Mode" => T("passive (side by side)"),
        "EDR Block Mode" => T("EDR block mode"),
        "Not running" => T("not running"),
        _ => mode,
    };

    public string YesNo(bool value) => value ? T("yes") : T("no");

    public Color OnOffColor(bool? value) => value switch { true => Color.Success, false => Color.Error, null => Color.Default };

    public string Change(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => T("new"),
        ChangeKind.Removed => T("removed"),
        ChangeKind.Changed => T("changed"),
        _ => kind.ToString(),
    };

    /// <summary>Bei Software sagt "aktualisiert" mehr als "geändert".</summary>
    public string Change(InventoryChange change) => IsDowngrade(change) ? T("older version") : IsUpgrade(change) ? T("updated") : Change(change.Kind);

    private static bool IsUpgrade(InventoryChange c) => c is { Category: "Software", Kind: ChangeKind.Changed, OldValue: { } old, NewValue: { } updated } && VersionComparer.Compare(old, updated) < 0;

    private static bool IsDowngrade(InventoryChange c) => c is { Category: "Software", Kind: ChangeKind.Changed, OldValue: { } old, NewValue: { } updated } && VersionComparer.Compare(old, updated) > 0;

    public Color ChangeColor(InventoryChange change) => IsDowngrade(change) ? Color.Warning : ChangeColor(change.Kind);

    public Color ChangeColor(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => Color.Success,
        ChangeKind.Removed => Color.Error,
        _ => Color.Info,
    };

    /// <summary>Alt → Neu, je nachdem, welche Seite vorhanden ist.</summary>
    /// <param name="translate">Übersetzt feste Werte wie "on"/"off"; Versionsnummern und Namen bleiben unverändert, weil es dafür keine Übersetzung gibt.</param>
    public string ChangeValues(InventoryChange c, Func<string, string>? translate = null)
    {
        translate ??= v => v;
        return (c.OldValue, c.NewValue) switch
        {
            (null, null) => "",
            (null, var added) => translate(added!),
            (var removed, null) => translate(removed!),
            var (old, updated) => $"{translate(old)} → {translate(updated)}",
        };
    }

    public string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KB",
        _ => $"{bytes} Bytes",
    };

    /// <summary>Dateiart des Setups, wie sie im Paket landet.</summary>
    public string InstallerFormat(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "wix" or "msi" => "MSI",
        "" => "?",
        _ => "EXE",
    };

    /// <summary>Womit das Setup gebaut ist; daraus ergeben sich die Silent-Parameter.</summary>
    public string InstallerTechnology(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "wix" => "Windows Installer (WiX)",
        "msi" => "Windows Installer",
        "burn" => "WiX Burn",
        "inno" => "Inno Setup",
        "nullsoft" => "Nullsoft (NSIS)",
        "exe" => T("vendor-specific"),
        _ => type ?? "",
    };

    public string InstallerScope(string? scope) => (scope ?? "").ToLowerInvariant() switch
    {
        "machine" => T("all users"),
        "user" => T("logged-on user only"),
        _ => "",
    };

    /// <summary>Adressen aus fremden Quellen nur als Link anbieten, wenn sie wirklich ins Web zeigen.</summary>
    public string? WebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;

    /// <summary>Wochentage, Montag zuerst.</summary>
    public static readonly DayOfWeek[] WeekDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Abgekürzter Wochentag in der aktuellen Sprache (Mo/Mon).</summary>
    public string Day(DayOfWeek day) => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day).TrimEnd('.');

    public string Trigger(Schedule s) => s.Trigger switch
    {
        ScheduleTrigger.Once => T("once on {0}", s.StartLocal is { } start ? LocalTime(start) : "–"),
        ScheduleTrigger.Interval => s.IntervalMinutes % 60 == 0 ? T("every {0} hours", s.IntervalMinutes / 60) : T("every {0} minutes", s.IntervalMinutes),
        ScheduleTrigger.Daily => T("daily at {0}", s.TimeOfDay.ToString(@"hh\:mm")),
        ScheduleTrigger.Weekly => T("{0} at {1}", string.Join(", ", WeekDays.Where(d => (s.DaysOfWeek & (1 << (int)d)) != 0).Select(Day)), s.TimeOfDay.ToString(@"hh\:mm")),
        _ => s.Trigger.ToString(),
    };
}
