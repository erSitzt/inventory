using System.Text.RegularExpressions;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <param name="Reason">Woran die Art erkannt wurde (kurze, technische Belege wie "Printer MIB" oder "Port 9100").</param>
public sealed record DeviceClassification(DeviceKind Kind, string? Reason, string? Manufacturer, string? Model, string? Os);

/// <summary>
/// Schätzt aus dem, was ein Gerät ohne Anmeldung preisgibt (SNMP, offene Ports, Banner, MAC-Hersteller), um welche Art Gerät es
/// sich handelt. Belastbare Belege (Drucker-MIB, Windows-Name über SMB) gehen vor Hinweisen wie einzelnen Ports.
/// </summary>
public static class DeviceClassifier
{
    /// <summary>
    /// Private Enterprise Numbers (IANA) aus sysObjectID: Hersteller und, wo eindeutig, die Geräteart. Net-SNMP steckt in vielen
    /// Linux-Geräten (NAS, Appliances) und sagt nichts über den Hersteller; dort bleibt der Name leer.
    /// </summary>
    private static readonly Dictionary<int, (string Vendor, DeviceKind Kind)> Enterprises = new()
    {
        [2] = ("IBM", DeviceKind.Unknown),
        [9] = ("Cisco", DeviceKind.Unknown),
        [11] = ("HP", DeviceKind.Unknown),
        [43] = ("3Com", DeviceKind.Switch),
        [171] = ("D-Link", DeviceKind.Unknown),
        [207] = ("Allied Telesis", DeviceKind.Switch),
        [232] = ("HPE", DeviceKind.Management),
        [253] = ("Xerox", DeviceKind.Printer),
        [311] = ("Microsoft", DeviceKind.Windows),
        [318] = ("APC", DeviceKind.Ups),
        [367] = ("Ricoh", DeviceKind.Printer),
        [368] = ("Axis", DeviceKind.Camera),
        [534] = ("Eaton", DeviceKind.Ups),
        [641] = ("Lexmark", DeviceKind.Printer),
        [674] = ("Dell", DeviceKind.Unknown),
        [705] = ("Eaton", DeviceKind.Ups),
        [789] = ("NetApp", DeviceKind.Storage),
        [872] = ("AVM", DeviceKind.Router),
        [890] = ("Zyxel", DeviceKind.Unknown),
        [1248] = ("Epson", DeviceKind.Printer),
        [1347] = ("Kyocera", DeviceKind.Printer),
        [1588] = ("Brocade", DeviceKind.Switch),
        [1602] = ("Canon", DeviceKind.Printer),
        [1916] = ("Extreme Networks", DeviceKind.Switch),
        [1991] = ("Foundry", DeviceKind.Switch),
        [2011] = ("Huawei", DeviceKind.Unknown),
        [2021] = ("", DeviceKind.Linux),
        [2356] = ("LANCOM", DeviceKind.Router),
        [2385] = ("Sharp", DeviceKind.Printer),
        [2435] = ("Brother", DeviceKind.Printer),
        [2604] = ("Sophos", DeviceKind.Firewall),
        [2620] = ("Check Point", DeviceKind.Firewall),
        [2636] = ("Juniper", DeviceKind.Unknown),
        [3224] = ("Juniper", DeviceKind.Firewall),
        [3375] = ("F5", DeviceKind.Unknown),
        [4413] = ("Broadcom", DeviceKind.Switch),
        [4526] = ("Netgear", DeviceKind.Unknown),
        [5624] = ("Enterasys", DeviceKind.Switch),
        [5951] = ("Citrix", DeviceKind.Unknown),
        [6574] = ("Synology", DeviceKind.Storage),
        [6876] = ("VMware", DeviceKind.Hypervisor),
        [8072] = ("", DeviceKind.Linux),
        [8741] = ("SonicWall", DeviceKind.Firewall),
        [11863] = ("TP-Link", DeviceKind.Unknown),
        [12356] = ("Fortinet", DeviceKind.Firewall),
        [14179] = ("Cisco", DeviceKind.AccessPoint),
        [14823] = ("Aruba", DeviceKind.AccessPoint),
        [14988] = ("MikroTik", DeviceKind.Router),
        [18334] = ("Konica Minolta", DeviceKind.Printer),
        [24681] = ("QNAP", DeviceKind.Storage),
        [25053] = ("Ruckus", DeviceKind.AccessPoint),
        [25461] = ("Palo Alto Networks", DeviceKind.Firewall),
        [25506] = ("H3C", DeviceKind.Switch),
        [29671] = ("Meraki", DeviceKind.Unknown),
        [30065] = ("Arista", DeviceKind.Switch),
        [39165] = ("Hikvision", DeviceKind.Camera),
        [41112] = ("Ubiquiti", DeviceKind.Unknown),
    };

    /// <summary>Schlüsselwörter in sysDescr, HTTP-Titel, Anmelde-Realm, Zertifikat oder MAC-Hersteller; die erste passende Regel zählt.</summary>
    private static readonly (Regex Pattern, DeviceKind Kind, string? Vendor)[] Keywords =
    [
        (Words("laserjet|officejet|deskjet|pagewide|designjet|imagerunner|i-sensys|bizhub|taskalfa|ecosys|workcentre|versalink|altalink|aficio|mfp|printer|drucker|imageclass"), DeviceKind.Printer, null),
        (Words("ilo|integrated lights-out|idrac|ipmi|bmc|imm2?|xclarity|irmc"), DeviceKind.Management, null),
        (Words("esxi|vmware esx|vcenter"), DeviceKind.Hypervisor, "VMware"),
        (Words("proxmox"), DeviceKind.Hypervisor, "Proxmox"),
        (Words("hyper-v"), DeviceKind.Hypervisor, "Microsoft"),
        (Words("diskstation|rackstation|synology"), DeviceKind.Storage, "Synology"),
        (Words("qnap|qts"), DeviceKind.Storage, "QNAP"),
        (Words("truenas|freenas|readynas|terastation|my cloud|nas"), DeviceKind.Storage, null),
        (Words("fortigate|fortios"), DeviceKind.Firewall, "Fortinet"),
        (Words("pfsense"), DeviceKind.Firewall, "Netgate"),
        (Words("opnsense"), DeviceKind.Firewall, "OPNsense"),
        (Words("sophos|utm|xg firewall"), DeviceKind.Firewall, "Sophos"),
        (Words("sonicwall|sonicos"), DeviceKind.Firewall, "SonicWall"),
        (Words("firewall|asa"), DeviceKind.Firewall, null),
        (Words("fritz!box|fritzbox"), DeviceKind.Router, "AVM"),
        (Words("routeros|mikrotik"), DeviceKind.Router, "MikroTik"),
        (Words("unifi ap|uap|access point|accesspoint|aironet|instant on ap"), DeviceKind.AccessPoint, null),
        (Words("smart-ups|back-ups|symmetra|ups|usv|network management card"), DeviceKind.Ups, null),
        (Words("ip camera|network camera|netzwerkkamera|webcam|nvr|hikvision|dahua"), DeviceKind.Camera, null),
        (Words("yealink|snom|polycom|grandstream|gigaset|ip phone|voip"), DeviceKind.Phone, null),
        (Words("switch|procurve|catalyst|nexus|aruba [0-9]{4}|powerconnect|smart managed|managed switch"), DeviceKind.Switch, null),
        (Words("router|ios xe|edgerouter|vigor|draytek"), DeviceKind.Router, null),
        (Words("windows"), DeviceKind.Windows, "Microsoft"),
        (Words("espressif|shelly|tasmota|sonos|chromecast|philips hue|signify|tuya"), DeviceKind.Iot, null),
    ];

    /// <summary>Hersteller laut MAC-Adresse, deren Geräte fast immer einer Art angehören.</summary>
    private static readonly (string Vendor, DeviceKind Kind)[] KindsByMacVendor =
    [
        ("Brother", DeviceKind.Printer), ("Kyocera", DeviceKind.Printer), ("Lexmark", DeviceKind.Printer), ("Xerox", DeviceKind.Printer),
        ("Ricoh", DeviceKind.Printer), ("Konica Minolta", DeviceKind.Printer), ("Seiko Epson", DeviceKind.Printer), ("Sharp", DeviceKind.Printer),
        ("Synology", DeviceKind.Storage), ("QNAP", DeviceKind.Storage),
        ("Hikvision", DeviceKind.Camera), ("Hangzhou Hikvision", DeviceKind.Camera), ("Axis Communications", DeviceKind.Camera), ("Dahua", DeviceKind.Camera),
        ("Yealink", DeviceKind.Phone), ("Snom", DeviceKind.Phone), ("Polycom", DeviceKind.Phone), ("Grandstream", DeviceKind.Phone),
        ("American Power Conversion", DeviceKind.Ups), ("Schneider Electric", DeviceKind.Ups),
        ("Ubiquiti", DeviceKind.AccessPoint), ("Ruckus", DeviceKind.AccessPoint), ("Aruba", DeviceKind.AccessPoint),
        ("Fortinet", DeviceKind.Firewall), ("Palo Alto", DeviceKind.Firewall), ("Sophos", DeviceKind.Firewall), ("SonicWall", DeviceKind.Firewall),
        ("AVM", DeviceKind.Router), ("MikroTik", DeviceKind.Router), ("Routerboard", DeviceKind.Router),
        ("Espressif", DeviceKind.Iot), ("Shelly", DeviceKind.Iot), ("Sonos", DeviceKind.Iot), ("Tuya", DeviceKind.Iot),
        ("Raspberry Pi", DeviceKind.Linux), ("Apple", DeviceKind.Apple),
        ("VMware", DeviceKind.Unknown),
    ];

    public static DeviceClassification Classify(DeviceObservation o, string? macVendor)
    {
        var snmp = o.Snmp;
        var enterprise = Enterprise(snmp?.SysObjectId);
        var (enterpriseVendor, enterpriseKind) = enterprise is { } number && Enterprises.TryGetValue(number, out var known) ? known : (null, DeviceKind.Unknown);
        var macKind = macVendor is null ? DeviceKind.Unknown : KindsByMacVendor.FirstOrDefault(v => macVendor.Contains(v.Vendor, StringComparison.OrdinalIgnoreCase)).Kind;
        var os = GuessOs(o);
        var manufacturer = Clean(snmp?.Manufacturer) ?? Clean(enterpriseVendor);
        var model = Clean(snmp?.Model);

        DeviceClassification Result(DeviceKind kind, string reason, string? vendor = null)
        {
            var maker = manufacturer ?? vendor ?? ShortVendor(macVendor);
            // "Linux/Unix" nur aus einem SSH-Banner geschätzt: bei Hypervisoren und Verwaltungsschnittstellen nichtssagend.
            var system = os == "Linux/Unix" && kind is not (DeviceKind.Linux or DeviceKind.Storage or DeviceKind.Unknown) ? DefaultOs(kind, maker) : os ?? DefaultOs(kind, maker);
            return new(kind, reason, maker, model, system);
        }

        // Belastbare Belege: das Gerät sagt selbst, was es ist.
        if (snmp?.IsPrinter == true)
            return Result(DeviceKind.Printer, "Printer MIB");
        if (snmp?.IsUps == true)
            return Result(DeviceKind.Ups, "UPS MIB");

        // sysDescr ist der aussagekräftigste Text; danach Weboberfläche, Zertifikat, Banner.
        foreach (var (text, source) in new[] { (snmp?.SysDescr, "sysDescr"), (o.HttpTitle, "HTTP title"), (o.HttpRealm, "HTTP realm"), (o.TlsSubject, "TLS certificate"), (o.HttpServer, "HTTP server"), (o.Banner, "banner") })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            foreach (var (pattern, kind, vendor) in Keywords)
            {
                if (!pattern.IsMatch(text))
                    continue;
                // "Windows" in einem Seitentitel sagt nichts über das Gerät selbst.
                if (kind == DeviceKind.Windows && source != "sysDescr" && source != "HTTP server")
                    continue;
                return Result(kind, $"{source}: {pattern.Match(text).Value}", vendor);
            }
        }

        if (enterpriseKind != DeviceKind.Unknown)
            return Result(enterpriseKind, $"sysObjectID: {(string.IsNullOrEmpty(enterpriseVendor) ? "Net-SNMP" : enterpriseVendor)}");

        if (snmp is not null)
        {
            if (snmp.BridgePorts > 0 && snmp.Forwarding != true)
                return Result(DeviceKind.Switch, "Bridge MIB");
            if (snmp.Forwarding == true && snmp.BridgePorts is null or 0 && enterprise is not (8072 or 2021))
                return Result(DeviceKind.Router, "IP forwarding");
            if (snmp.BridgePorts > 0)
                return Result(DeviceKind.Switch, "Bridge MIB");
        }

        // Auch Samba und NAS-Systeme nennen über SMB einen Namen; ein SSH-Banner ohne Windows verrät sie.
        if (o.WindowsName is not null && (os is null or "Windows"))
            return Result(DeviceKind.Windows, "SMB", "Microsoft");

        // Ports und MAC-Hersteller sind nur Hinweise.
        var ports = o.OpenPorts;
        if (ports.Contains(9100) || (ports.Contains(515) && ports.Contains(631)))
            return Result(DeviceKind.Printer, ports.Contains(9100) ? "Port 9100" : "Ports 515/631");
        if (ports.Contains(902) && ports.Contains(443))
            return Result(DeviceKind.Hypervisor, "Port 902", "VMware");
        if (ports.Contains(8006))
            return Result(DeviceKind.Hypervisor, "Port 8006", "Proxmox");
        if (macKind is not DeviceKind.Unknown and not DeviceKind.Apple and not DeviceKind.Linux)
            return Result(macKind, $"MAC: {ShortVendor(macVendor)}");
        if (ports.Contains(554) && !ports.Contains(445))
            return Result(DeviceKind.Camera, "Port 554");
        if (ports.Contains(5060) && !ports.Contains(445))
            return Result(DeviceKind.Phone, "Port 5060");
        if (ports.Contains(3389) || (ports.Contains(135) && ports.Contains(445)) || ports.Contains(5985))
            return Result(DeviceKind.Windows, ports.Contains(3389) ? "Port 3389" : ports.Contains(5985) ? "Port 5985" : "Ports 135/445", "Microsoft");
        if (macKind == DeviceKind.Apple || ports.Contains(62078) || ports.Contains(548))
            return Result(DeviceKind.Apple, macKind == DeviceKind.Apple ? "MAC: Apple" : ports.Contains(62078) ? "Port 62078" : "Port 548", "Apple");
        if ((os is not null && os != "Windows") || enterprise is 8072 or 2021 || macKind == DeviceKind.Linux)
            return Result(DeviceKind.Linux, o.SshBanner is not null ? "SSH banner" : snmp is not null ? "sysDescr" : $"MAC: {ShortVendor(macVendor)}");
        if (ports.Contains(22))
            return Result(DeviceKind.Linux, "Port 22");
        return Result(DeviceKind.Unknown, macVendor is null ? "" : $"MAC: {ShortVendor(macVendor)}");
    }

    /// <summary>Die Private Enterprise Number aus einer sysObjectID (1.3.6.1.4.1.&lt;n&gt;…).</summary>
    public static int? Enterprise(string? sysObjectId)
    {
        const string prefix = "1.3.6.1.4.1.";
        if (sysObjectId is null || !sysObjectId.TrimStart('.').StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = sysObjectId.TrimStart('.')[prefix.Length..];
        var end = rest.IndexOf('.');
        return int.TryParse(end < 0 ? rest : rest[..end], out var number) ? number : null;
    }

    /// <summary>Betriebssystem laut SSH-Banner, sysDescr oder Webserver.</summary>
    public static string? GuessOs(DeviceObservation o)
    {
        if (o.SshBanner is { } ssh)
        {
            foreach (var (marker, name) in new[]
            {
                ("ubuntu", "Ubuntu"), ("debian", "Debian"), ("raspbian", "Raspberry Pi OS"), ("freebsd", "FreeBSD"), ("openbsd", "OpenBSD"),
                ("netbsd", "NetBSD"), ("for_windows", "Windows"), ("rosssh", "RouterOS"), ("cisco", "Cisco IOS"), ("dropbear", "Embedded Linux"),
                ("fedora", "Fedora"), ("el7", "Red Hat Enterprise Linux 7"), ("el8", "Red Hat Enterprise Linux 8"), ("el9", "Red Hat Enterprise Linux 9"),
            })
            {
                if (ssh.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }

        if (o.Snmp?.SysDescr is { } descr)
        {
            if (Regex.Match(descr, @"^Linux \S+ (\d+\.\d+[\w.\-]*)") is { Success: true } linux)
                return $"Linux {linux.Groups[1].Value}";
            if (Regex.Match(descr, @"Windows Version [\d.]+ \(Build (\d+)", RegexOptions.IgnoreCase) is { Success: true } windows)
                return $"Windows (Build {windows.Groups[1].Value})";
            if (Regex.Match(descr, @"Cisco IOS[^,]*,.*?Version ([^,\s]+)", RegexOptions.Singleline) is { Success: true } ios)
                return $"Cisco IOS {ios.Groups[1].Value}";
            foreach (var name in new[] { "FreeBSD", "RouterOS", "FortiOS", "Junos", "ArubaOS", "EOS", "NX-OS", "DSM", "QTS", "VMware ESXi" })
            {
                if (Regex.Match(descr, $@"\b{Regex.Escape(name)}\b[ \-v]*([\d.]+\w*)?", RegexOptions.IgnoreCase) is { Success: true } match)
                    return match.Groups[1].Success ? $"{name} {match.Groups[1].Value}" : name;
            }
        }

        if (o.HttpServer?.Contains("Microsoft-IIS", StringComparison.OrdinalIgnoreCase) == true || o.WindowsName is not null)
            return "Windows";
        if (o.SshBanner is not null)
            return "Linux/Unix";
        return null;
    }

    private static string? DefaultOs(DeviceKind kind, string? vendor) => kind switch
    {
        DeviceKind.Windows => "Windows",
        DeviceKind.Hypervisor when vendor == "VMware" => "VMware ESXi",
        DeviceKind.Hypervisor when vendor == "Proxmox" => "Proxmox VE",
        _ => null,
    };

    /// <summary>"Hewlett Packard Enterprise" → "Hewlett Packard Enterprise"; Firmenzusätze wie "Co., Ltd." entfallen.</summary>
    public static string? ShortVendor(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return null;
        var cleaned = Regex.Replace(vendor, @"[,.]?\s+(co\.?,?\s*ltd\.?|corporation|incorporated|corp\.?|inc\.?|ltd\.?|gmbh(\s*&\s*co\.?\s*kg)?|ag|llc|s\.?a\.?|b\.?v\.?|limited|technologies|technology|electronics|international)\b\.?", "", RegexOptions.IgnoreCase);
        return cleaned.Trim(' ', ',', '.') is { Length: > 0 } result ? result : vendor.Trim();
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Regex Words(string alternatives) =>
        new($@"(?<![\w])({alternatives})(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
