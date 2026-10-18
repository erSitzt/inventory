using System.Net;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Liefert je nach letztem Oktett der Adresse ein typisches Gerät; für die Entwicklung ohne echtes Netz (z. B. unter WSL).</summary>
public sealed class SimulatedDeviceProbe : IDeviceProbe
{
    public async Task<DeviceObservation> ProbeAsync(IPAddress address, IReadOnlyList<string> communities, CancellationToken ct = default)
    {
        await Task.Delay(20, ct);
        var ip = address.ToString();
        var octet = address.GetAddressBytes()[3];
        var snmp = communities.Count > 0;
        var o = new DeviceObservation { IpAddress = ip };
        // In der Filiale ist .1 ein eigener Router, über eine Transferstrecke mit der Firewall des Hauptsitzes verbunden.
        if (address.GetAddressBytes()[1] == 20 && octet == 1)
        {
            Set(o, [22, 80, 443], "rt-hamburg", "SSH-2.0-OpenSSH_7.4", "LANCOM 1793VA");
            if (snmp)
                o.Snmp = new SnmpFacts { SysName = "rt-hamburg", SysDescr = "LANCOM 1793VA (over ISDN) 10.80.0154 / 04.07.2024", SysObjectId = "1.3.6.1.4.1.2356.11.1.5",
                    Forwarding = true, Model = "LANCOM 1793VA", SerialNumber = "4003123456100011", InterfaceAddresses = ["10.20.0.1/24", "172.16.0.2/30"] };
            return o;
        }
        switch (octet)
        {
            case 1:
                Set(o, [22, 443], "fw-01", ssh: null, title: "FortiGate - Login");
                if (snmp)
                    o.Snmp = new SnmpFacts { SysName = "fw-01", SysDescr = "FortiGate-60F v7.2.8,build1639,240313 (GA.M)", SysObjectId = "1.3.6.1.4.1.12356.101.1.6005", SysLocation = "Server room", Forwarding = true, SerialNumber = "FGT60FTK2209XXXX", Model = "FortiGate-60F", UptimeSeconds = 3_600_000,
                        InterfaceAddresses = ["10.0.0.1/24", "10.0.1.1/24", "172.16.0.1/30"] };
                break;
            case 2:
            case 3:
                Set(o, [22, 80, 443], $"sw-0{octet - 1}", null, "HPE Aruba 2930F");
                if (snmp)
                    o.Snmp = new SnmpFacts { SysName = $"sw-0{octet - 1}", SysDescr = "Aruba JL259A 2930F-24G-4SFP Switch, revision WC.16.11.0016, ROM WC.16.01.0009", SysObjectId = "1.3.6.1.4.1.11.2.3.7.11.181.21", BridgePorts = 28, Forwarding = false, InterfaceCount = 30, Model = "Aruba JL259A 2930F-24G-4SFP Switch", SerialNumber = $"CN0{octet}KXXXXX", Manufacturer = "HPE", Firmware = "WC.16.11.0016", SysLocation = "Floor 1", UptimeSeconds = 12_000_000 };
                break;
            case 10:
                Set(o, [80, 443, 515, 631, 9100], "npi3c4f2a", null, "HP LaserJet Pro M404dn");
                if (snmp)
                    o.Snmp = new SnmpFacts
                    {
                        SysName = "NPI3C4F2A", SysDescr = "HP ETHERNET MULTI-ENVIRONMENT,ROM none,JETDIRECT,JD153,EEPROM JSI24090006", SysObjectId = "1.3.6.1.4.1.11.2.3.9.1", IsPrinter = true,
                        Model = "HP LaserJet Pro M404dn", SerialNumber = "PHBLL12345", PageCount = 48213, SysLocation = "Office 2.14",
                        Supplies = [new("Black Cartridge HP CF259A", 18, 100)],
                    };
                o.MacAddress = "3C:52:82:4F:2A:10";
                break;
            case 11:
                Set(o, [80, 443, 515, 631, 9100], "brn-hl-l5100", null, "Brother HL-L5100DN series");
                o.MacAddress = "30:05:5C:12:34:56";
                if (snmp)
                    o.Snmp = new SnmpFacts
                    {
                        SysName = "BRN30055C123456", SysDescr = "Brother NC-8700h, Firmware Ver.1.13  (20.05.15),MID 8CE-G16,FID 2", SysObjectId = "1.3.6.1.4.1.2435.2.3.9.1", IsPrinter = true,
                        Model = "Brother HL-L5100DN series", SerialNumber = "E78172K0N123456", PageCount = 15402, SysLocation = "Reception",
                        Supplies = [new("Black Toner Cartridge", 64, 100), new("Drum Unit", 22, 100)],
                    };
                break;
            case 12:
                Set(o, [80, 443, 9100], "kyo-taskalfa", null, "TASKalfa 2553ci");
                o.MacAddress = "00:17:C8:AA:BB:12";
                if (snmp)
                    o.Snmp = new SnmpFacts
                    {
                        SysName = "KM5A1B2C", SysDescr = "KYOCERA Document Solutions Printing System", SysObjectId = "1.3.6.1.4.1.1347.41", IsPrinter = true,
                        Model = "TASKalfa 2553ci", SerialNumber = "W8Y1234567", PageCount = 231_877, SysLocation = "Floor 2 copy room",
                        Supplies = [new("Black Toner", 71, 100), new("Cyan Toner", 9, 100), new("Magenta Toner", 46, 100), new("Yellow Toner", 52, 100), new("Waste Toner Box", -3, -2)],
                    };
                break;
            case 20:
                Set(o, [22, 80, 139, 445, 443, 5000, 5001], "nas-01", "SSH-2.0-OpenSSH_8.2", "Synology DiskStation - nas-01");
                o.MacAddress = "00:11:32:AA:BB:20";
                o.WindowsName = "NAS-01";
                if (snmp)
                    o.Snmp = new SnmpFacts { SysName = "nas-01", SysDescr = "Linux nas-01 4.4.302+ #69057 SMP Fri Jan 12 17:01:39 CST 2024 x86_64", SysObjectId = "1.3.6.1.4.1.8072.3.2.10", Model = "DS920+", SerialNumber = "2080PDN123456", Firmware = "DSM 7.2.1-69057", UptimeSeconds = 7_900_000 };
                break;
            case 21:
                Set(o, [22, 80, 443, 902], "esx-01", "SSH-2.0-OpenSSH_9.3", "VMware ESXi");
                o.TlsSubject = "CN=esx-01.corp.example, O=VMware";
                break;
            case 22:
                Set(o, [22, 8006], "pve-01", "SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3", "pve-01 - Proxmox Virtual Environment");
                break;
            case 30:
                Set(o, [22, 80, 443], "web-01", "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.10", "Welcome to nginx!");
                o.HttpServer = "nginx/1.18.0 (Ubuntu)";
                if (snmp)
                    o.Snmp = new SnmpFacts { SysName = "web-01", SysDescr = "Linux web-01 5.15.0-112-generic #122-Ubuntu SMP Thu May 23 07:48:21 UTC 2024 x86_64", SysObjectId = "1.3.6.1.4.1.8072.3.2.10", UptimeSeconds = 2_100_000 };
                break;
            case 31:
                Set(o, [22, 5432], "db-01", "SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3", null);
                break;
            case 40:
                Set(o, [80, 443], "ups-01", null, "Network Management Card");
                o.MacAddress = "28:29:86:AA:BB:40";
                if (snmp)
                    o.Snmp = new SnmpFacts { SysName = "ups-01", SysDescr = "APC Web/SNMP Management Card (MB:v4.1.0 PF:v6.9.6 PN:apc_hw05_aos_696.bin)", SysObjectId = "1.3.6.1.4.1.318.1.3.27", IsUps = true, Manufacturer = "APC", Model = "Smart-UPS 1500", SerialNumber = "AS1234567890", Firmware = "UPS 09.3" };
                break;
            case 50:
            case 51:
                Set(o, [22, 443], $"ap-0{octet - 49}", "SSH-2.0-dropbear_2020.81", null);
                o.MacAddress = $"74:83:C2:AA:BB:{octet}";
                break;
            case 60:
                Set(o, [80, 443, 554], "cam-entrance", null, null);
                o.HttpRealm = "DS-2CD2143G2-I";
                o.MacAddress = "C0:56:E3:AA:BB:60";
                break;
            case 70:
            case 71:
                Set(o, [80, 443, 5060], null, null, "Yealink T54W Phone");
                o.MacAddress = $"80:5E:C0:AA:BB:{octet}";
                break;
            case 80:
                Set(o, [22, 80, 443], "srv-01-ilo", "SSH-2.0-mpSSH_0.2.1", "iLO 5");
                break;
            case 90:
                Set(o, [80], null, null, null);
                o.MacAddress = "24:0A:C4:AA:BB:90";
                break;
            case 101:
            case 102:
            case 103:
                Set(o, [135, 139, 445, 3389], null, null, null);
                o.WindowsName = $"PC-{octet}";
                o.ReverseName = $"pc-{octet}.corp.example";
                break;
            case 150:
                // Ein Windows-Rechner, der nicht im Inventar steht.
                Set(o, [135, 139, 445], null, null, null);
                o.WindowsName = "LAPTOP-GUEST7";
                break;
            default:
                break;
        }
        // Gleiche Geräte in verschiedenen Netzen bekommen verschiedene MAC-Adressen.
        o.MacAddress = o.MacAddress?.Replace(":AA:", $":{address.GetAddressBytes()[2]:X2}:");
        return o;
    }

    /// <summary>Der simulierte Server steht im Servernetz 10.0.1.0/24 hinter der Firewall; die Filiale liegt hinter einem weiteren Router.</summary>
    public Task<TraceResult> TraceAsync(IPAddress target, CancellationToken ct = default)
    {
        var bytes = target.GetAddressBytes();
        IReadOnlyList<string?> hops = (bytes[1], bytes[2]) switch
        {
            (0, 1) => [],
            (20, _) => ["10.0.1.1", "172.16.0.2"],
            _ => ["10.0.1.1"],
        };
        return Task.FromResult(new TraceResult(hops, true));
    }

    public IReadOnlyDictionary<string, string> LocalNeighbors() => new Dictionary<string, string>();

    /// <summary>Der simulierte Server steht im Servernetz.</summary>
    public IReadOnlyList<(string Ip, int Prefix)> LocalAddresses() => [("10.0.1.5", 24)];

    private static void Set(DeviceObservation o, int[] ports, string? name, string? ssh, string? title)
    {
        o.PingReply = true;
        o.OpenPorts = [.. ports];
        o.ReverseName = name is null ? null : $"{name}.corp.example";
        o.SshBanner = ssh;
        o.HttpTitle = title;
    }
}
