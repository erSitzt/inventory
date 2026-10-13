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

    public string AlertKind(AlertKind kind) => kind switch
    {
        Inventory.Core.Model.AlertKind.CollectionEntered => T("A computer enters a collection"),
        Inventory.Core.Model.AlertKind.CollectionCount => T("A collection reaches a number of members"),
        Inventory.Core.Model.AlertKind.DeploymentFailed => T("A deployment finishes with failures"),
        Inventory.Core.Model.AlertKind.ScheduleFailed => T("A schedule fails or is missed"),
        Inventory.Core.Model.AlertKind.AdSyncFailed => T("The Active Directory synchronization fails"),
        Inventory.Core.Model.AlertKind.BackupFailed => T("The database backup fails"),
        Inventory.Core.Model.AlertKind.UpdateFailed => T("The update of the server fails"),
        Inventory.Core.Model.AlertKind.NewComputer => T("A computer is added"),
        Inventory.Core.Model.AlertKind.ScanErrors => T("Scan errors reach a number of computers"),
        Inventory.Core.Model.AlertKind.LicenseExceeded => T("A licence is exceeded"),
        _ => kind.ToString(),
    };

    /// <summary>Der Auslöser einer Regel samt Sammlung und Schwelle.</summary>
    public string Alert(AlertRule rule)
    {
        var text = AlertKind(rule.Kind);
        if (AlertRule.NeedsCollection(rule.Kind))
            text += $": {rule.Collection?.Name ?? "–"}";
        if (AlertRule.NeedsThreshold(rule.Kind))
            text += $" (≥ {rule.Threshold})";
        return text;
    }

    public string Report(ReportSource source) => source switch
    {
        ReportSource.Computers => T("Computers"),
        ReportSource.Software => T("Installed software"),
        ReportSource.PendingUpdates => T("Pending updates"),
        ReportSource.Deployments => T("Deployments"),
        ReportSource.Changes => T("Changes"),
        _ => source.ToString(),
    };

    public string Webhook(WebhookFormat format) => format switch
    {
        WebhookFormat.Teams => "Microsoft Teams",
        WebhookFormat.Slack => "Slack",
        _ => T("Generic (JSON)"),
    };

    public string Channel(NotificationChannel channel) =>
        channel.Kind == NotificationKind.Email ? T("E-mail") : $"{T("Webhook")} – {Webhook(channel.WebhookFormat)}";

    public string Agentless(AgentlessPolicy policy) => policy switch
    {
        AgentlessPolicy.Always => T("allowed for all jobs"),
        AgentlessPolicy.AgentInstallOnly => T("only to install the agent"),
        AgentlessPolicy.Never => T("never"),
        _ => policy.ToString(),
    };

    /// <summary>Ein Agent gilt als verbunden, solange sein letzter Kontakt nur wenige Minuten zurückliegt.</summary>
    public bool AgentConnected(Computer c) => c.AgentLastSeenUtc is { } seen && DateTime.UtcNow - seen < TimeSpan.FromMinutes(5);

    /// <summary>Datum und Uhrzeit in der Schreibweise der aktuellen Sprache.</summary>
    /// <summary>
    /// Zeigt einen UTC-Zeitpunkt in Ortszeit. Werte aus der Datenbank tragen keine Kennzeichnung und gelten als UTC.
    /// Bewusst nur diese eine Überladung: eine zweite für nicht-nullbare Werte würde von allen Pflichtfeldern (CreatedUtc,
    /// TimeUtc) gewählt und zeigte sie unumgerechnet an.
    /// </summary>
    public string LocalTime(DateTime? utc) =>
        utc is not { } value ? "–" : (value.Kind == DateTimeKind.Local ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()).ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Zeigt eine Uhrzeit, die schon in der Ortszeit des Servers gespeichert ist (z. B. <see cref="Schedule.StartLocal"/>).</summary>
    public string WallTime(DateTime local) => local.ToString("g", CultureInfo.CurrentCulture);

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
        RuleField.RdpEnabled => T("Remote Desktop enabled"),
        RuleField.RdpWithoutNla => T("Remote Desktop without network level authentication"),
        RuleField.UacOff => T("User Account Control off"),
        RuleField.Smb1Enabled => T("SMB 1 enabled"),
        RuleField.CredentialGuard => T("Credential Guard running"),
        RuleField.MemoryIntegrity => T("Memory integrity on"),
        RuleField.LsaProtection => T("LSA protection on"),
        RuleField.BuiltinAdminEnabled => T("Built-in administrator account active"),
        RuleField.BuiltinAdminPasswordAgeDays => T("Built-in administrator: password age (days)"),
        RuleField.NotActivated => T("Windows not activated"),
        RuleField.PendingSecurityUpdates => T("Pending security updates (count)"),
        RuleField.Hotfix => T("Hotfix installed (KB number)"),
        RuleField.PendingUpdate => T("Pending update (KB number or title)"),
        RuleField.DirectoryOu => T("AD organizational unit"),
        RuleField.DirectoryGroup => T("AD group (direct member)"),
        RuleField.DaysSinceDirectoryLogon => T("Days since last AD logon"),
        RuleField.DirectoryDescription => T("AD description"),
        RuleField.Tag => T("Tag"),
        RuleField.Location => T("Location"),
        RuleField.Owner => T("Owner"),
        RuleField.CostCenter => T("Cost center"),
        RuleField.InventoryNumber => T("Inventory number"),
        RuleField.PrimaryUser => T("Primary user"),
        RuleField.WarrantyDaysLeft => T("Warranty: days left"),
        RuleField.Domain => T("Domain"),
        RuleField.SerialNumber => T("Serial number"),
        RuleField.CpuName => T("Processor"),
        RuleField.OsArchitecture => T("Architecture"),
        RuleField.IpAddress => T("IP address"),
        RuleField.DiskFreeGb => T("Free space on the system drive (GB)"),
        RuleField.DaysSinceBoot => T("Days since last boot"),
        RuleField.ScanAgeDays => T("Days since last scan"),
        RuleField.AgentPresent => T("Agent installed"),
        RuleField.Service => T("Windows service"),
        RuleField.InCollection => T("Member of collection"),
        RuleField.DeviceType => T("Device type"),
        RuleField.SecureBoot => T("Secure Boot on"),
        RuleField.TpmReady => T("TPM ready"),
        RuleField.SystemDriveProtected => T("BitLocker (system drive)"),
        RuleField.FirmwareType => T("Firmware"),
        RuleField.Gpu => T("Graphics"),
        RuleField.BatteryHealthPercent => T("Battery health (%)"),
        RuleField.DiskHealthProblem => T("Physical disk reports a problem"),
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
            or RuleField.FirewallOff or RuleField.AntivirusProblem or RuleField.RebootRequired
            or RuleField.RdpEnabled or RuleField.RdpWithoutNla or RuleField.UacOff or RuleField.Smb1Enabled or RuleField.CredentialGuard or RuleField.MemoryIntegrity
            or RuleField.LsaProtection or RuleField.BuiltinAdminEnabled or RuleField.NotActivated
            or RuleField.AgentPresent or RuleField.SecureBoot or RuleField.TpmReady or RuleField.SystemDriveProtected or RuleField.DiskHealthProblem => [RuleOperator.Equals],
        RuleField.InCollection or RuleField.DeviceType => [RuleOperator.Equals, RuleOperator.NotEquals],
        RuleField.DiskFreeGb or RuleField.BatteryHealthPercent or RuleField.DaysSinceBoot or RuleField.ScanAgeDays => [RuleOperator.LessThan, RuleOperator.GreaterOrEqual],
        RuleField.Service => [RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.Contains],
        RuleField.IpAddress => [RuleOperator.StartsWith, RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.Contains],
        RuleField.PendingUpdates or RuleField.PendingSecurityUpdates or RuleField.DaysSinceUpdate or RuleField.BuiltinAdminPasswordAgeDays
            or RuleField.DaysSinceDirectoryLogon => [RuleOperator.GreaterOrEqual, RuleOperator.LessThan, RuleOperator.Equals],
        RuleField.MemoryGb => [RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals],
        RuleField.OsVersion => [RuleOperator.StartsWith, RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals, RuleOperator.Contains],
        RuleField.Hotfix => [RuleOperator.Equals, RuleOperator.NotEquals],
        RuleField.WarrantyDaysLeft => [RuleOperator.LessThan, RuleOperator.GreaterOrEqual],
        RuleField.Tag => [RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.Contains, RuleOperator.NotContains],
        RuleField.PendingUpdate => [RuleOperator.Contains, RuleOperator.NotContains, RuleOperator.Equals, RuleOperator.NotEquals],
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
        StepType.Uninstall => T("Uninstall"),
        StepType.WindowsUpdate => T("Windows Update"),
        StepType.WakeOnLan => "Wake-on-LAN",
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
        StepType.Uninstall => Icons.Material.Filled.DeleteSweep,
        StepType.WindowsUpdate => Icons.Material.Filled.SystemUpdateAlt,
        _ => Icons.Material.Filled.PlayArrow,
    };

    private string UpdateStepSummary(JobStep step)
    {
        if (step.UpdateKbIds.Count > 0)
            return string.Join(", ", step.UpdateKbIds);
        var scope = step.UpdateSecurityOnly ? T("security and critical updates") : T("all pending updates");
        return step.UpdateIncludeDrivers ? T("{0}, including drivers", scope) : scope;
    }

    /// <summary>Kurzbeschreibung dessen, was ein Schritt tut, für Listen.</summary>
    public string StepSummary(JobStep step) => step.Type switch
    {
        StepType.Install => $"{step.File} {step.Arguments}".Trim(),
        StepType.FileCopy => $"{step.File} → {step.Destination}",
        StepType.Service => $"{ServiceActionName(step.ServiceAction)}: {step.ServiceName}",
        StepType.Sleep => T("{0} seconds", step.Seconds),
        StepType.Reboot => step.Seconds > 0 ? T("after {0} seconds", step.Seconds) : T("immediately"),
        StepType.InventoryScan => "",
        StepType.WindowsUpdate => UpdateStepSummary(step),
        StepType.Uninstall => $"{(string.IsNullOrWhiteSpace(step.ProductCode) ? step.SoftwareName : step.ProductCode)} {step.Arguments}".Trim(),
        _ => step.Script.Split('\n')[0].Trim(),
    };

    public string Role(AccessRole role) => role switch
    {
        AccessRole.Admin => T("Administrator"),
        AccessRole.Operator => T("Operator"),
        AccessRole.Viewer => T("Viewer"),
        _ => T("none"),
    };

    public string Source(AuditSource source) => source switch
    {
        AuditSource.Web => T("Web interface"),
        AuditSource.Api => "API",
        AuditSource.ApiToken => T("API token"),
        AuditSource.Agent => T("Agent"),
        AuditSource.System => T("Server"),
        _ => "",
    };

    public string RoleHint(AccessRole role) => role switch
    {
        AccessRole.Admin => T("Everything, including packages, credentials, settings and commands on target computers."),
        AccessRole.Operator => T("View everything; scan, deploy existing packages, cancel and retry deployments, remote access."),
        _ => T("View everything, change and run nothing."),
    };

    /// <summary>Wartungsfenster in einer Zeile, z. B. "Mo, Di 19:00–06:00".</summary>
    public string Window(MaintenanceWindow? window) => window is null || (window.DaysOfWeek & 0x7F) == 0
        ? T("none")
        : $"{string.Join(", ", WeekDays.Where(d => (window.DaysOfWeek & (1 << (int)d)) != 0).Select(Day))} {window.From:hh\\:mm}–{window.To:hh\\:mm}";

    /// <summary>Kurzfassung einer Paket-Erkennung, z. B. für das Protokoll.</summary>
    public string Detection(StepConditions detection) => string.Join(", ", new[]
    {
        string.IsNullOrWhiteSpace(detection.SoftwareInstalled) ? null
            : string.IsNullOrWhiteSpace(detection.SoftwareMinVersion) ? detection.SoftwareInstalled : $"{detection.SoftwareInstalled} ≥ {detection.SoftwareMinVersion}",
        detection.MsiProductCode, detection.FileExists, detection.RegistryKeyExists,
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

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
        TargetStatus.WaitingReboot => T("restarting"),
        TargetStatus.AlreadyInstalled => T("already installed"),
        TargetStatus.WaitingWindow => T("waiting for maintenance window"),
        TargetStatus.WaitingPilot => T("waiting for pilot group"),
        _ => status.ToString(),
    };

    public Color StatusColor(TargetStatus status) => status switch
    {
        TargetStatus.Running => Color.Info,
        TargetStatus.Succeeded => Color.Success,
        TargetStatus.Failed => Color.Error,
        TargetStatus.Cancelled => Color.Warning,
        TargetStatus.WaitingOffline => Color.Warning,
        TargetStatus.WaitingReboot => Color.Info,
        TargetStatus.AlreadyInstalled => Color.Success,
        TargetStatus.WaitingWindow => Color.Warning,
        TargetStatus.WaitingPilot => Color.Default,
        _ => Color.Default,
    };

    public string Status(StepStatus status) => status switch
    {
        StepStatus.Succeeded => T("succeeded"),
        StepStatus.Failed => T("failed"),
        StepStatus.Skipped => T("skipped"),
        StepStatus.Deferred => T("after restart"),
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
        or RuleField.FirewallOff or RuleField.AntivirusProblem or RuleField.RebootRequired
        or RuleField.RdpEnabled or RuleField.RdpWithoutNla or RuleField.UacOff or RuleField.Smb1Enabled or RuleField.CredentialGuard or RuleField.MemoryIntegrity
        or RuleField.LsaProtection or RuleField.BuiltinAdminEnabled or RuleField.NotActivated
        or RuleField.AgentPresent or RuleField.SecureBoot or RuleField.TpmReady or RuleField.SystemDriveProtected or RuleField.DiskHealthProblem;

    /// <summary>Der Wert einer Regel, wie er in Listen erscheint: Namen statt Kennungen, übersetzte feste Werte.</summary>
    public string RuleValue(CollectionRule rule, Func<int, string?> collectionName) => rule.Field switch
    {
        RuleField.InCollection => int.TryParse(rule.Value, out var id) ? collectionName(id) ?? T("(deleted collection)") : rule.Value,
        RuleField.DeviceType => Enum.TryParse<ChassisKind>(rule.Value, true, out var kind) ? T(Core.Chassis.Label(kind)) : rule.Value,
        RuleField.Service when !string.IsNullOrWhiteSpace(rule.Value2) => $"{rule.Value} ({ServiceState(rule.Value2)})",
        _ when IsFlag(rule.Field) => rule.Value.Trim().ToLowerInvariant() is "true" or "ja" or "1" ? T("yes") : T("no"),
        _ => rule.Value,
    };

    public string ServiceState(string? state) => state switch { "Running" => T("running"), "Stopped" => T("stopped"), _ => state ?? "" };

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

    public Color SeverityColor(string? severity) => severity switch { "Critical" => Color.Error, "Important" => Color.Warning, null => Color.Default, _ => Color.Info };

    public string UacLevel(int? level) => level is >= 0 and <= 3 ? T(InventoryDiff.UacLevels[level.Value]) : T("unknown");

    public string Chassis(Computer computer) => T(Core.Chassis.Label(Core.Chassis.Kind(computer.ChassisType, computer.Model)));

    /// <summary>Zustand eines Datenträgers laut Windows-Speicherverwaltung.</summary>
    public Color HealthColor(string? health) => health switch { "Healthy" => Color.Success, "Warning" => Color.Warning, "Unhealthy" => Color.Error, _ => Color.Default };

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
        ScheduleTrigger.Once => T("once on {0}", s.StartLocal is { } start ? WallTime(start) : "–"),
        ScheduleTrigger.Interval => s.IntervalMinutes % 60 == 0 ? T("every {0} hours", s.IntervalMinutes / 60) : T("every {0} minutes", s.IntervalMinutes),
        ScheduleTrigger.Daily => T("daily at {0}", s.TimeOfDay.ToString(@"hh\:mm")),
        ScheduleTrigger.Weekly => T("{0} at {1}", string.Join(", ", WeekDays.Where(d => (s.DaysOfWeek & (1 << (int)d)) != 0).Select(Day)), s.TimeOfDay.ToString(@"hh\:mm")),
        ScheduleTrigger.Monthly => T("monthly, {0} at {1}", MonthlyDay(s), s.TimeOfDay.ToString(@"hh\:mm")),
        ScheduleTrigger.OnMembership => s.RecheckDays > 0 ? T("required for every member, checked again every {0} days", s.RecheckDays) : T("required for every member"),
        _ => s.Trigger.ToString(),
    } + (s.EndLocal is { } end ? ", " + T("until {0}", end.ToString("d")) : "");

    /// <summary>"am 15.", "am 2. Dienstag" oder "3 Tage nach dem 2. Dienstag".</summary>
    private string MonthlyDay(Schedule s)
    {
        var day = s.MonthDay > 0
            ? T("on day {0}", s.MonthDay)
            : s.MonthWeek >= 5 ? T("on the last {0}", WeekdayName(s.MonthWeekday)) : T("on the {0}. {1}", s.MonthWeek, WeekdayName(s.MonthWeekday));
        return s.MonthOffsetDays > 0 ? T("{0} days after: {1}", s.MonthOffsetDays, day) : day;
    }

    public string WeekdayName(int day) => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)(((day % 7) + 7) % 7));
}
