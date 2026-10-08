namespace Inventory.Core;

/// <summary>Befehle, Dateien und Links, mit denen ein Administrator von seinem PC aus auf einen Rechner zugreift.</summary>
public static class RemoteAccessLinks
{
    /// <summary>Inhalt einer .rdp-Datei; Windows öffnet sie mit der Remotedesktopverbindung.</summary>
    public static string RdpFile(string host) =>
        $"full address:s:{host}\r\nprompt for credentials:i:1\r\nauthentication level:i:2\r\n";

    /// <summary>Windows-Remoteunterstützung anbieten: der Benutzer sieht eine Anfrage und stimmt zu.</summary>
    public static string OfferAssistance(string host) => $"msra.exe /offerRA {host}";

    /// <summary>Sitzung des angemeldeten Benutzers per RDP spiegeln und steuern.</summary>
    public static string Shadow(string host, int sessionId) => $"mstsc.exe /v:{host} /shadow:{sessionId} /control";

    /// <summary>Zeigt die Sitzungen des Rechners, falls die beim Scan erfasste Nummer nicht mehr stimmt.</summary>
    public static string ListSessions(string host) => $"qwinsta /server:{host}";

    public static string TeamViewer(string id) => $"teamviewer10://control?device={Uri.EscapeDataString(id)}";

    public static string AnyDesk(string id) => $"anydesk:{Uri.EscapeDataString(id)}";
}
