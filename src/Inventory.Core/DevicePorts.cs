namespace Inventory.Core;

/// <summary>Die TCP-Ports, die der Gerätescan prüft, mit dem üblichen Dienst dahinter.</summary>
public static class DevicePorts
{
    public static readonly IReadOnlyDictionary<int, string> Services = new SortedDictionary<int, string>
    {
        [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [135] = "RPC", [139] = "NetBIOS",
        [443] = "HTTPS", [445] = "SMB", [515] = "LPD", [548] = "AFP", [554] = "RTSP", [631] = "IPP", [902] = "VMware",
        [1883] = "MQTT", [3306] = "MySQL", [3389] = "RDP", [5000] = "HTTP", [5001] = "HTTPS", [5060] = "SIP", [5432] = "PostgreSQL",
        [5900] = "VNC", [5985] = "WinRM", [8006] = "Proxmox", [8080] = "HTTP", [8443] = "HTTPS", [9100] = "JetDirect", [62078] = "iOS",
    };

    public static readonly int[] Http = [80, 8080, 5000];
    public static readonly int[] Https = [443, 8443, 5001, 8006];

    public static string Describe(int port) => Services.TryGetValue(port, out var name) ? $"{port} ({name})" : port.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
