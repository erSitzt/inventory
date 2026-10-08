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
        RuleField.Online or RuleField.Scanned => [RuleOperator.Equals],
        RuleField.MemoryGb => [RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals],
        RuleField.OsVersion => [RuleOperator.StartsWith, RuleOperator.LessThan, RuleOperator.GreaterOrEqual, RuleOperator.Equals, RuleOperator.Contains],
        RuleField.Software => [RuleOperator.Contains, RuleOperator.NotContains, RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.StartsWith],
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
        _ => status.ToString(),
    };

    public static Color StatusColor(TargetStatus status) => status switch
    {
        TargetStatus.Running => Color.Info,
        TargetStatus.Succeeded => Color.Success,
        TargetStatus.Failed => Color.Error,
        TargetStatus.Cancelled => Color.Warning,
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

    public static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KB",
        _ => $"{bytes} Bytes",
    };
}
