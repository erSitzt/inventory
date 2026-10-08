using Inventory.Core.Model;

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
}
