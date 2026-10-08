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
}
