using Inventory.Contracts;
using Inventory.Core.Model;
using MudBlazor;

namespace Inventory.Server.Components;

/// <summary>Anzeigetexte für die Oberfläche.</summary>
public static class Display
{
    public static string Transport(TransportKind kind) => kind switch
    {
        TransportKind.SmbService => "SMB + Dienst",
        TransportKind.WinRm => "WinRM",
        TransportKind.Agent => "Agent",
        _ => kind.ToString(),
    };

    /// <summary>Ein Agent gilt als verbunden, solange sein letzter Kontakt nur wenige Minuten zurückliegt.</summary>
    public static bool AgentConnected(Computer c) => c.AgentLastSeenUtc is { } seen && DateTime.UtcNow - seen < TimeSpan.FromMinutes(5);

    public static string LocalTime(DateTime? utc) =>
        utc is null ? "–" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public static string Bytes(long? bytes)
    {
        if (bytes is null)
            return "–";
        var gb = bytes.Value / 1024d / 1024 / 1024;
        return gb >= 10 ? $"{gb:N0} GB" : $"{gb:N1} GB";
    }

    public static string Os(Computer c) =>
        c.OsName is null ? "–" : $"{c.OsName.Replace("Microsoft ", "")} {c.OsRelease}".Trim();

    public static string Field(RuleField field) => field switch
    {
        RuleField.ComputerName => "Rechnername",
        RuleField.NameMismatch => "Gemeldeter Name weicht ab",
        RuleField.MissingInDirectory => "Nicht (mehr) im Active Directory",
        RuleField.LocalAdmin => "Lokaler Administrator",
        RuleField.FirewallOff => "Firewall (teilweise) aus",
        RuleField.AntivirusProblem => "Kein Echtzeit-Virenschutz",
        RuleField.RebootRequired => "Neustart ausstehend",
        RuleField.PendingUpdates => "Ausstehende Updates (Anzahl)",
        RuleField.DaysSinceUpdate => "Tage seit letzter Update-Installation",
        RuleField.Online => "Online",
        RuleField.Scanned => "Gescannt",
        RuleField.OsName => "Betriebssystem",
        RuleField.OsVersion => "Betriebssystem-Version",
        RuleField.OsRelease => "Funktionsupdate (z. B. 24H2)",
        RuleField.Manufacturer => "Hersteller",
        RuleField.Model => "Modell",
        RuleField.LoggedOnUser => "Angemeldeter Benutzer",
        RuleField.MemoryGb => "Arbeitsspeicher (GB)",
        RuleField.Software => "Software",
        _ => field.ToString(),
    };

    public static string Operator(RuleOperator op) => op switch
    {
        RuleOperator.Contains => "enthält",
        RuleOperator.NotContains => "enthält nicht",
        RuleOperator.Equals => "ist gleich",
        RuleOperator.NotEquals => "ist ungleich",
        RuleOperator.StartsWith => "beginnt mit",
        RuleOperator.LessThan => "kleiner als",
        RuleOperator.GreaterOrEqual => "größer oder gleich",
        _ => op.ToString(),
    };

    /// <summary>Operatoren, die für ein Feld sinnvoll sind.</summary>
    public static IReadOnlyList<RuleOperator> OperatorsFor(RuleField field) => field switch
    {
        RuleField.Online or RuleField.Scanned or RuleField.NameMismatch or RuleField.MissingInDirectory
            or RuleField.FirewallOff or RuleField.AntivirusProblem or RuleField.RebootRequired => [RuleOperator.Equals],
        RuleField.PendingUpdates or RuleField.DaysSinceUpdate => [RuleOperator.GreaterOrEqual, RuleOperator.LessThan, RuleOperator.Equals],
        RuleField.MemoryGb => [RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals],
        RuleField.OsVersion => [RuleOperator.StartsWith, RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals, RuleOperator.Contains],
        RuleField.Software or RuleField.LocalAdmin => [RuleOperator.Contains, RuleOperator.NotContains, RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.StartsWith],
        _ => [RuleOperator.Contains, RuleOperator.NotContains, RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.StartsWith],
    };

    public static string Step(StepType type) => type switch
    {
        StepType.Command => "Befehl (cmd)",
        StepType.PowerShell => "PowerShell",
        StepType.Install => "Installation",
        StepType.FileCopy => "Dateien kopieren",
        StepType.Service => "Dienst",
        StepType.Sleep => "Warten",
        StepType.Reboot => "Neustart",
        StepType.InventoryScan => "Inventar-Scan",
        _ => type.ToString(),
    };

    public static string StepIcon(StepType type) => type switch
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
    public static string StepSummary(JobStep step) => step.Type switch
    {
        StepType.Install => $"{step.File} {step.Arguments}".Trim(),
        StepType.FileCopy => $"{step.File} → {step.Destination}",
        StepType.Service => $"{ServiceActionName(step.ServiceAction)}: {step.ServiceName}",
        StepType.Sleep => $"{step.Seconds} Sekunden",
        StepType.Reboot => step.Seconds > 0 ? $"nach {step.Seconds} Sekunden" : "sofort",
        StepType.InventoryScan => "",
        _ => step.Script.Split('\n')[0].Trim(),
    };

    public static string RunAs(StepRunAs runAs) => runAs switch
    {
        StepRunAs.System => "Lokales System",
        StepRunAs.LoggedOnUser => "Angemeldeter Benutzer",
        _ => runAs.ToString(),
    };

    /// <summary>Schritt-Typen, bei denen sich der Ausführungskontext wählen lässt.</summary>
    public static bool SupportsRunAs(StepType type) => type is StepType.Command or StepType.PowerShell or StepType.Install;

    public static string ServiceActionName(ServiceAction action) => action switch
    {
        ServiceAction.Start => "Starten",
        ServiceAction.Stop => "Beenden",
        ServiceAction.Restart => "Neu starten",
        _ => action.ToString(),
    };

    public static string Status(TargetStatus status) => status switch
    {
        TargetStatus.Queued => "wartet",
        TargetStatus.Running => "läuft",
        TargetStatus.Succeeded => "erfolgreich",
        TargetStatus.Failed => "fehlgeschlagen",
        TargetStatus.Cancelled => "abgebrochen",
        TargetStatus.WaitingOffline => "wartet auf Rechner",
        _ => status.ToString(),
    };

    public static Color StatusColor(TargetStatus status) => status switch
    {
        TargetStatus.Running => Color.Info,
        TargetStatus.Succeeded => Color.Success,
        TargetStatus.Failed => Color.Error,
        TargetStatus.Cancelled => Color.Warning,
        TargetStatus.WaitingOffline => Color.Warning,
        _ => Color.Default,
    };

    public static string Status(StepStatus status) => status switch
    {
        StepStatus.Succeeded => "erfolgreich",
        StepStatus.Failed => "fehlgeschlagen",
        StepStatus.Skipped => "übersprungen",
        _ => status.ToString(),
    };

    public static Color StatusColor(StepStatus status) => status switch
    {
        StepStatus.Succeeded => Color.Success,
        StepStatus.Failed => Color.Error,
        _ => Color.Default,
    };

    /// <summary>Felder, die nur "ja" oder "nein" kennen.</summary>
    public static bool IsFlag(RuleField field) => field is RuleField.Online or RuleField.Scanned or RuleField.NameMismatch or RuleField.MissingInDirectory
        or RuleField.FirewallOff or RuleField.AntivirusProblem or RuleField.RebootRequired;

    public static string OnOff(bool? value) => value switch { true => "an", false => "aus", null => "unbekannt" };

    public static Color OnOffColor(bool? value) => value switch { true => Color.Success, false => Color.Error, null => Color.Default };

    public static string Change(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "neu",
        ChangeKind.Removed => "entfernt",
        ChangeKind.Changed => "geändert",
        _ => kind.ToString(),
    };

    /// <summary>Bei Software sagt "aktualisiert" mehr als "geändert".</summary>
    public static string Change(InventoryChange change)
    {
        if (change is not { Category: "Software", Kind: ChangeKind.Changed, OldValue: { } old, NewValue: { } updated })
            return Change(change.Kind);
        return Inventory.Contracts.VersionComparer.Compare(old, updated) switch
        {
            < 0 => "aktualisiert",
            > 0 => "ältere Version",
            _ => "geändert",
        };
    }

    public static Color ChangeColor(InventoryChange change) =>
        change.Kind == ChangeKind.Changed && Change(change) == "ältere Version" ? Color.Warning : ChangeColor(change.Kind);

    public static Color ChangeColor(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => Color.Success,
        ChangeKind.Removed => Color.Error,
        _ => Color.Info,
    };

    /// <summary>Alt → Neu, je nachdem, welche Seite vorhanden ist.</summary>
    public static string ChangeValues(InventoryChange c) => (c.OldValue, c.NewValue) switch
    {
        (null, null) => "",
        (null, var added) => added!,
        (var removed, null) => removed!,
        var (old, updated) => $"{old} → {updated}",
    };

    public static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KB",
        _ => $"{bytes} Bytes",
    };

    /// <summary>Dateiart des Setups, wie sie im Paket landet.</summary>
    public static string InstallerFormat(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "wix" or "msi" => "MSI",
        "" => "?",
        _ => "EXE",
    };

    /// <summary>Womit das Setup gebaut ist; daraus ergeben sich die Silent-Parameter.</summary>
    public static string InstallerTechnology(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "wix" => "Windows Installer (WiX)",
        "msi" => "Windows Installer",
        "burn" => "WiX Burn",
        "inno" => "Inno Setup",
        "nullsoft" => "Nullsoft (NSIS)",
        "exe" => "herstellereigen",
        _ => type ?? "",
    };

    public static string InstallerScope(string? scope) => (scope ?? "").ToLowerInvariant() switch
    {
        "machine" => "alle Benutzer",
        "user" => "nur angemeldeter Benutzer",
        _ => "",
    };

    /// <summary>Adressen aus fremden Quellen nur als Link anbieten, wenn sie wirklich ins Web zeigen.</summary>
    public static string? WebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;

    private static readonly string[] DayNames = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

    /// <summary>Wochentage in deutscher Reihenfolge (Montag zuerst).</summary>
    public static readonly DayOfWeek[] WeekDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string Day(DayOfWeek day) => DayNames[(int)day];

    public static string Trigger(Schedule s) => s.Trigger switch
    {
        ScheduleTrigger.Once => $"einmalig am {s.StartLocal:dd.MM.yyyy HH:mm}",
        ScheduleTrigger.Interval => s.IntervalMinutes % 60 == 0 ? $"alle {s.IntervalMinutes / 60} Stunden" : $"alle {s.IntervalMinutes} Minuten",
        ScheduleTrigger.Daily => $"täglich um {s.TimeOfDay:hh\\:mm}",
        ScheduleTrigger.Weekly => $"{string.Join(", ", WeekDays.Where(d => (s.DaysOfWeek & (1 << (int)d)) != 0).Select(Day))} um {s.TimeOfDay:hh\\:mm}",
        _ => s.Trigger.ToString(),
    };
}
