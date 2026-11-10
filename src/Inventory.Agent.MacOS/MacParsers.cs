using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inventory.Contracts;

namespace Inventory.Agent.MacOS;

/// <summary>
/// Liest die Ausgaben der macOS-Programme (system_profiler, sw_vers, dscl, fdesetup …). Reine Funktionen ohne Systemzugriff,
/// damit sie sich mit aufgezeichneten Ausgaben prüfen lassen.
/// </summary>
internal static partial class MacParsers
{
    /// <summary>Namen der macOS-Versionen; neuere ohne bekannten Namen erscheinen nur mit Nummer.</summary>
    private static readonly Dictionary<int, string> VersionNames = new()
    {
        [11] = "Big Sur", [12] = "Monterey", [13] = "Ventura", [14] = "Sonoma", [15] = "Sequoia", [26] = "Tahoe",
    };

    /// <summary>"macOS Sequoia 15.1" aus sw_vers; dazu Version mit Build, z. B. "15.1 (24B83)".</summary>
    public static (string? Name, string? Version) Os(string? swVers)
    {
        var values = KeyValues(swVers, ':');
        var product = values.GetValueOrDefault("ProductName") ?? "macOS";
        if (!values.TryGetValue("ProductVersion", out var version))
            return (null, null);
        var major = int.TryParse(version.Split('.')[0], out var m) ? m : 0;
        var name = VersionNames.TryGetValue(major, out var label) ? $"{product} {label} {version}" : $"{product} {version}";
        return (name, values.TryGetValue("BuildVersion", out var build) ? $"{version} ({build})" : version);
    }

    /// <summary>Zeilen der Form "Schlüssel: Wert" (Leerraum um den Wert entfernt).</summary>
    public static Dictionary<string, string> KeyValues(string? text, char separator)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(text))
        {
            var index = line.IndexOf(separator);
            if (index > 0)
                result.TryAdd(line[..index].Trim(), line[(index + 1)..].Trim());
        }
        return result;
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    /// <summary>Die Einträge eines Datentyps aus der JSON-Ausgabe von system_profiler; leer, wenn er fehlt.</summary>
    public static List<JsonElement> Items(JsonDocument? document, string dataType) =>
        document is not null && document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty(dataType, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList()
            : [];

    public static JsonDocument? Json(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static IEnumerable<JsonElement> Children(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("_items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    /// <summary>Hardware-Übersicht (SPHardwareDataType).</summary>
    public sealed record HardwareOverview(string? ModelName, string? ModelIdentifier, string? SerialNumber, string? Cpu, string? Firmware, string? HardwareUuid);

    public static HardwareOverview? Hardware(JsonDocument? document)
    {
        if (Items(document, "SPHardwareDataType").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } item)
            return null;
        // Apple Silicon nennt den Chip, Intel-Macs Prozessor und Takt.
        var cpu = Text(item, "chip_type")
                  ?? (Text(item, "cpu_type") is { } type ? (Text(item, "current_processor_speed") is { } speed ? $"{type} @ {speed}" : type) : null);
        return new HardwareOverview(Text(item, "machine_name"), Text(item, "machine_model"), Text(item, "serial_number"), cpu,
            Text(item, "boot_rom_version"), Text(item, "platform_UUID"));
    }

    /// <summary>
    /// Der Verkaufsname, z. B. "MacBook Pro (14-inch, Nov 2023)", aus <c>ioreg -arc IOPlatformDevice -k product-name</c>
    /// (eine Property List; der Name steht dort als Base64). Nur Apple Silicon liefert ihn.
    /// </summary>
    public static string? ProductName(string? ioregPlist)
    {
        if (string.IsNullOrEmpty(ioregPlist))
            return null;
        var match = ProductNamePattern().Match(ioregPlist);
        if (!match.Success)
            return null;
        try
        {
            var name = Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(match.Groups[1].Value, @"\s", ""))).TrimEnd('\0').Trim();
            return name.Length > 0 ? name : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<key>product-name</key>\s*<data>([^<]*)</data>")]
    private static partial Regex ProductNamePattern();

    /// <summary>Wert einer Zeile <c>"Schlüssel" = "Wert"</c> aus ioreg.</summary>
    public static string? IoregValue(string? text, string key)
    {
        var match = Regex.Match(text ?? "", "\"" + Regex.Escape(key) + "\"\\s*=\\s*\"([^\"]*)\"");
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Gehäusetyp nach SMBIOS aus dem Modell, damit Macs in dieselben Geräteklassen fallen wie PCs: Notebook (10),
    /// All-in-One (13), Desktop (3).
    /// </summary>
    public static int? ChassisType(string? modelName, string? modelIdentifier)
    {
        var name = modelName ?? "";
        var id = modelIdentifier ?? "";
        if (name.StartsWith("MacBook", StringComparison.OrdinalIgnoreCase) || id.StartsWith("MacBook", StringComparison.OrdinalIgnoreCase))
            return 10;
        if (name.StartsWith("iMac", StringComparison.OrdinalIgnoreCase) || id.StartsWith("iMac", StringComparison.OrdinalIgnoreCase))
            return 13;
        if (name.StartsWith("Mac", StringComparison.OrdinalIgnoreCase))
            return 3;
        return null;
    }

    /// <summary>Programme aus SPApplicationsDataType: was in /Applications oder ~/Applications liegt, keine Bestandteile des Systems.</summary>
    public static List<SoftwareItem> Applications(JsonDocument? document)
    {
        var result = new List<SoftwareItem>();
        foreach (var app in Items(document, "SPApplicationsDataType"))
        {
            var name = Text(app, "_name");
            var path = Text(app, "path");
            if (name is null || path is null || !IsInstalledApplication(path, out var perUser))
                continue;
            var architecture = Architecture(Text(app, "arch_kind"));
            result.Add(new SoftwareItem
            {
                Name = name,
                Version = Text(app, "version"),
                Publisher = Publisher(app),
                InstallDate = DateTime.TryParse(Text(app, "lastModified"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var modified) ? modified.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : null,
                Is64Bit = Text(app, "arch_kind") != "arch_i32",
                PerUser = perUser,
                Origin = Origin(Text(app, "obtained_from")),
                Architecture = architecture,
            });
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Herkunft laut system_profiler ("obtained_from").</summary>
    public static string? Origin(string? obtainedFrom) => obtainedFrom switch
    {
        "apple" => SoftwareOrigins.Apple,
        "mac_app_store" or "ios_app_store" => SoftwareOrigins.AppStore,
        "identified_developer" => SoftwareOrigins.IdentifiedDeveloper,
        "unknown" => SoftwareOrigins.Unknown,
        _ => null,
    };

    /// <summary>Architektur laut system_profiler ("arch_kind"); 32-Bit-Intel-Programme starten auf aktuellen Macs gar nicht mehr.</summary>
    public static string? Architecture(string? archKind) => archKind switch
    {
        "arch_arm_i64" => SoftwareArchitectures.Universal,
        "arch_arm" => SoftwareArchitectures.AppleSilicon,
        "arch_i64" or "arch_i32" => SoftwareArchitectures.Intel,
        "arch_ios" => SoftwareArchitectures.IOS,
        _ => null,
    };

    /// <summary>Die Schnittstelle der Standardroute aus <c>route -n get default</c> ("interface: en0").</summary>
    public static string? DefaultInterface(string? routeOutput) =>
        KeyValues(routeOutput, ':').TryGetValue("interface", out var name) && name.Length > 0 ? name : null;

    /// <summary>Ein Beleg aus <c>pkgutil --pkg-info</c>: Kennung, Version und Installationszeit.</summary>
    public sealed record PackageReceipt(string Id, string? Version, DateTime? InstalledUtc);

    public static PackageReceipt? Receipt(string? pkgInfo)
    {
        var values = KeyValues(pkgInfo, ':');
        if (!values.TryGetValue("package-id", out var id) || id.Length == 0)
            return null;
        DateTime? installed = values.TryGetValue("install-time", out var time) && long.TryParse(time, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
        return new PackageReceipt(id, values.GetValueOrDefault("version") is { Length: > 0 } version ? version : null, installed);
    }

    /// <summary>
    /// Die Pakete, die neben den Programmen zählen: ohne die von Apple, und Update-Belege eines Pakets
    /// ("…AcrobatDCUpd2500121184", einer je Update) zusammengefasst zum neuesten. Andere Kennungen mit Ziffern am Ende bleiben getrennt.
    /// </summary>
    public static List<AppItem> Packages(IEnumerable<PackageReceipt> receipts) =>
        receipts
            .Where(r => !r.Id.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => UpdateSuffix().Replace(r.Id, ""), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.InstalledUtc ?? DateTime.MinValue).First())
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(r => new AppItem { Name = r.Id, Version = r.Version, Kind = AppKinds.InstallerPackage })
            .ToList();

    // Auch mit Sprachkürzel dahinter: "…AcroRdrSCADCUpd2600322075_MUI".
    [GeneratedRegex(@"(?<=Upd(?:ate)?)\d{4,}(?=(?:_[A-Za-z]+)?$)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateSuffix();

    /// <summary>
    /// Zeitpunkt der letzten Anmeldung aus der ersten Zeile von <c>last -1 &lt;name&gt;</c> ("max console Mon Nov 10 08:15 …").
    /// <c>last</c> nennt kein Jahr: es ist das Jahr, in dem der Tag zuletzt lag.
    /// </summary>
    public static DateTime? LastLogin(string? lastOutput, DateTime nowLocal, TimeZoneInfo zone)
    {
        var line = Lines(lastOutput).FirstOrDefault(l => !l.StartsWith("wtmp", StringComparison.Ordinal));
        var match = Regex.Match(line ?? "", @"\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) +(\d{1,2}) (\d{2}):(\d{2})");
        if (!match.Success)
            return null;
        var month = DateTime.ParseExact(match.Groups[1].Value, "MMM", CultureInfo.InvariantCulture).Month;
        int day = int.Parse(match.Groups[2].Value), hour = int.Parse(match.Groups[3].Value), minute = int.Parse(match.Groups[4].Value);
        var year = nowLocal.Year;
        if (day > DateTime.DaysInMonth(year, month) || new DateTime(year, month, day, hour, minute, 0) > nowLocal.AddDays(1))
            year--;
        if (day > DateTime.DaysInMonth(year, month))
            return null;
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), zone);
    }

    /// <summary>Wann das Kennwort zuletzt gesetzt wurde, aus <c>dscl . -readpl … accountPolicyData passwordLastSetTime</c>.</summary>
    public static DateTime? PasswordLastSet(string? dsclOutput)
    {
        var match = Regex.Match(dsclOutput ?? "", @"passwordLastSetTime:\s*([\d.]+)");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime
            : null;
    }

    /// <summary>Oberste Programme in /Applications (auch Unterordnern) oder im Programme-Ordner eines Benutzers, nicht in anderen Programmen.</summary>
    public static bool IsInstalledApplication(string path, out bool perUser)
    {
        perUser = path.StartsWith("/Users/", StringComparison.Ordinal) && path.Contains("/Applications/", StringComparison.Ordinal);
        if (!path.StartsWith("/Applications/", StringComparison.Ordinal) && !perUser)
            return false;
        var parent = path[..Math.Max(0, path.LastIndexOf('/'))];
        return !parent.Contains(".app/", StringComparison.OrdinalIgnoreCase) && !parent.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Der Entwickler aus der Signatur: "Developer ID Application: Google LLC (EQHXZ8M8AV)" → "Google LLC".</summary>
    public static string? Publisher(JsonElement app)
    {
        if (Text(app, "obtained_from") == "apple")
            return "Apple Inc.";
        if (app.ValueKind != JsonValueKind.Object || !app.TryGetProperty("signed_by", out var signers) || signers.ValueKind != JsonValueKind.Array)
            return null;
        var first = signers.EnumerateArray().Select(s => s.GetString()).FirstOrDefault();
        var match = Regex.Match(first ?? "", @"^(?:Developer ID Application|Apple Development|Apple Distribution|3rd Party Mac Developer Application):\s*(.+?)(?:\s*\([A-Z0-9]{10}\))?$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>Grafik und Bildschirme (SPDisplaysDataType); eingebaute Bildschirme zählen nicht als Monitore.</summary>
    public static (List<GpuItem> Gpus, List<MonitorItem> Monitors) Displays(JsonDocument? document)
    {
        var gpus = new List<GpuItem>();
        var monitors = new List<MonitorItem>();
        foreach (var gpu in Items(document, "SPDisplaysDataType"))
        {
            if ((Text(gpu, "sppci_model") ?? Text(gpu, "_name")) is { } model)
                gpus.Add(new GpuItem { Name = Text(gpu, "sppci_cores") is { } cores ? $"{model} ({cores} cores)" : model });
            if (gpu.ValueKind != JsonValueKind.Object || !gpu.TryGetProperty("spdisplays_ndrvs", out var displays) || displays.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var display in displays.EnumerateArray())
            {
                if ((Text(display, "spdisplays_connection_type") ?? "").Contains("internal", StringComparison.OrdinalIgnoreCase)
                    || (Text(display, "spdisplays_display_type") ?? "").Contains("built-in", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Text(display, "_name") is not { } name)
                    continue;
                monitors.Add(new MonitorItem
                {
                    Model = name,
                    SerialNumber = Text(display, "_spdisplays_display-serial-number"),
                    Year = int.TryParse(Text(display, "_spdisplays_display-year"), out var year) ? year : null,
                });
            }
        }
        return (gpus, monitors);
    }

    /// <summary>
    /// Arbeitsspeicher (SPMemoryDataType). Intel-Macs nennen Steckplätze mit Modulen; bei Apple Silicon sitzt der Speicher im Chip
    /// und erscheint als ein Eintrag.
    /// </summary>
    public static (int? Slots, List<MemoryModuleItem> Modules) Memory(JsonDocument? document)
    {
        var modules = new List<MemoryModuleItem>();
        int? slots = null;
        foreach (var entry in Items(document, "SPMemoryDataType"))
        {
            var children = Children(entry).ToList();
            if (children.Count > 0)
            {
                slots = (slots ?? 0) + children.Count;
                foreach (var slot in children)
                {
                    if (Size(Text(slot, "dimm_size")) is not { } size)
                        continue;
                    modules.Add(new MemoryModuleItem
                    {
                        Slot = Text(slot, "_name") ?? "",
                        CapacityBytes = size,
                        Manufacturer = Text(slot, "dimm_manufacturer"),
                        PartNumber = Text(slot, "dimm_part_number"),
                        SerialNumber = Text(slot, "dimm_serial_number"),
                        Type = Text(slot, "dimm_type"),
                        SpeedMhz = int.TryParse(Regex.Match(Text(slot, "dimm_speed") ?? "", @"\d+").Value, out var speed) ? speed : null,
                    });
                }
            }
            else if (Size(Text(entry, "SPMemoryDataType")) is { } total)
            {
                modules.Add(new MemoryModuleItem { Slot = "Unified memory", CapacityBytes = total, Manufacturer = Text(entry, "dimm_manufacturer"), Type = Text(entry, "dimm_type") });
            }
        }
        return (slots, modules);
    }

    /// <summary>"18 GB", "500,28 GB" oder "2 TB" in Bytes (Apple rechnet beim Arbeitsspeicher binär, bei Datenträgern dezimal).</summary>
    public static long? Size(string? text, bool binary = true)
    {
        var match = Regex.Match(text ?? "", @"^\s*([\d.,]+)\s*([KMGT])B", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        var unit = binary ? 1024d : 1000d;
        var exponent = "KMGT".IndexOf(char.ToUpperInvariant(match.Groups[2].Value[0])) + 1;
        return (long)Math.Round(value * Math.Pow(unit, exponent));
    }

    /// <summary>Eingebaute Datenträger (SPNVMeDataType, SPSerialATADataType).</summary>
    public static List<PhysicalDiskItem> Disks(JsonDocument? document)
    {
        var result = new List<PhysicalDiskItem>();
        foreach (var (type, bus) in new[] { ("SPNVMeDataType", "NVMe"), ("SPSerialATADataType", "SATA") })
        {
            foreach (var controller in Items(document, type))
            {
                foreach (var disk in Children(controller))
                {
                    var size = disk.TryGetProperty("size_in_bytes", out var bytes) && bytes.TryGetInt64(out var b) ? b : Size(Text(disk, "size"), binary: false) ?? 0;
                    // SATA-Controller nennen auch optische Laufwerke; ohne Größe sind sie kein Datenträger.
                    if (size <= 0)
                        continue;
                    result.Add(new PhysicalDiskItem
                    {
                        Model = Text(disk, "device_model") ?? Text(disk, "_name") ?? "",
                        SerialNumber = Text(disk, "device_serial"),
                        SizeBytes = size,
                        BusType = bus,
                        MediaType = Text(disk, "spsata_medium_type") is { } medium ? (medium.Contains("solid", StringComparison.OrdinalIgnoreCase) ? "SSD" : "HDD") : "SSD",
                        Health = Text(disk, "smart_status"),
                    });
                }
            }
        }
        return result;
    }

    /// <summary>Akku (SPPowerDataType): vorhanden, Zustand in Prozent der ursprünglichen Kapazität, Ladezyklen.</summary>
    public static (bool? Present, int? HealthPercent, int? Cycles) Battery(JsonDocument? document)
    {
        var items = Items(document, "SPPowerDataType");
        if (items.Count == 0)
            return (null, null, null);
        var battery = items.FirstOrDefault(i => Text(i, "_name") == "spbattery_information");
        if (battery.ValueKind != JsonValueKind.Object)
            return (false, null, null);
        int? health = null, cycles = null;
        if (battery.TryGetProperty("sppower_battery_health_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            cycles = int.TryParse(Text(info, "sppower_battery_cycle_count"), out var c) ? c : null;
            health = int.TryParse((Text(info, "sppower_battery_health_maximum_capacity") ?? "").TrimEnd('%', ' '), out var h) ? h : null;
        }
        return (true, health, cycles);
    }

    /// <summary>Drucker (SPPrintersDataType).</summary>
    public static List<PrinterItem> Printers(JsonDocument? document) =>
        Items(document, "SPPrintersDataType")
            .Where(p => Text(p, "_name") is not null)
            .Select(p =>
            {
                var uri = Text(p, "uri");
                return new PrinterItem
                {
                    Name = Text(p, "_name")!,
                    Driver = Text(p, "ppd") ?? Text(p, "driverversion"),
                    Port = uri,
                    Network = uri is not null && !uri.StartsWith("usb:", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase),
                    Shared = Text(p, "shared") == "yes",
                };
            })
            .ToList();

    /// <summary>"FileVault is On." / "FileVault is Off." / "Encryption in progress: Percent completed = 45.2".</summary>
    public static (bool? On, int? Percent) FileVault(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);
        var percent = Regex.Match(text, @"Percent completed\s*=\s*([\d.]+)");
        // Während der Ver- oder Entschlüsselung schützt FileVault (noch) nicht; der Anteil gilt nur beim Verschlüsseln.
        if (percent.Success && double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return (false, text.Contains("Decryption", StringComparison.OrdinalIgnoreCase) ? null : (int)p);
        if (text.Contains("FileVault is On", StringComparison.OrdinalIgnoreCase))
            return (true, 100);
        if (text.Contains("FileVault is Off", StringComparison.OrdinalIgnoreCase))
            return (false, 0);
        return (null, null);
    }

    /// <summary>
    /// Ein/aus aus Meldungen wie "Firewall is enabled. (State = 1)", "Firewall stealth mode is on", "assessments enabled" oder
    /// "System Integrity Protection status: enabled.".
    /// </summary>
    public static bool? Switch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var state = Regex.Match(text, @"State\s*=\s*(\d)");
        if (state.Success)
            return state.Groups[1].Value != "0";
        if (Regex.IsMatch(text, @"\b(disabled|off)\b", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(text, @"\b(enabled|on)\b", RegexOptions.IgnoreCase))
            return true;
        return null;
    }

    /// <summary>"1"/"0" (oder "true"/"false") aus <c>defaults read</c>; null, wenn der Wert fehlt.</summary>
    public static bool? Defaults(string? text) => text?.Trim() switch
    {
        "1" or "true" or "TRUE" or "YES" => true,
        "0" or "false" or "FALSE" or "NO" => false,
        _ => null,
    };

    /// <summary>Lokale Benutzer aus <c>dscl . -list /Users UniqueID</c>: echte Konten haben eine ID ab 501 und keinen Unterstrich vorn.</summary>
    public static List<string> Users(string? dsclList) =>
        Lines(dsclList)
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && int.TryParse(p[1], out var id) && id >= 501 && !p[0].StartsWith('_'))
            .Select(p => p[0])
            .ToList();

    /// <summary>Werte eines Attributs aus <c>dscl . -read …</c>: "RealName: Max" oder "RealName:\n Max Mustermann".</summary>
    public static List<string> DsclValues(string? text, string attribute)
    {
        var lines = Lines(text).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(attribute + ":", StringComparison.Ordinal))
                continue;
            var rest = lines[i][(attribute.Length + 1)..].Trim();
            if (rest.Length > 0)
                return rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            // Werte mit Leerzeichen stehen eingerückt in den Folgezeilen.
            return lines.Skip(i + 1).TakeWhile(l => l.StartsWith(' ')).Select(l => l.Trim()).ToList();
        }
        return [];
    }

    /// <summary>Gerätenamen und Anzeigenamen der Netzwerkanschlüsse aus <c>networksetup -listallhardwareports</c> (en0 → "Wi-Fi").</summary>
    public static Dictionary<string, string> HardwarePorts(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? port = null;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("Hardware Port:", StringComparison.Ordinal))
                port = line["Hardware Port:".Length..].Trim();
            else if (line.StartsWith("Device:", StringComparison.Ordinal) && port is not null)
                result.TryAdd(line["Device:".Length..].Trim(), port);
        }
        return result;
    }

    /// <summary>Startzeit aus <c>sysctl -n kern.boottime</c>: "{ sec = 1730360000, usec = 500000 } Thu Oct 31 …".</summary>
    public static DateTime? BootTime(string? text)
    {
        var match = Regex.Match(text ?? "", @"sec\s*=\s*(\d+)");
        return match.Success && long.TryParse(match.Groups[1].Value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
    }

    /// <summary>Wert einer Zeile "Name: Wert" aus <c>diskutil info</c>, z. B. "Volume Name".</summary>
    public static string? DiskutilValue(string? text, string key) =>
        KeyValues(text, ':').TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
