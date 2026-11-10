using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Inventory.Contracts;

namespace Inventory.Agent.MacOS;

/// <summary>
/// Erfasst das Inventar eines Macs mit den Programmen des Systems (system_profiler, sw_vers, sysctl, dscl …). Jeder Bereich für
/// sich: scheitert einer, steht das in den Warnungen, der Rest gilt. Was der Server sonst als "leer" speichern würde, darf
/// dabei nicht leer ankommen: ohne System oder Programme scheitert der ganze Scan (der Server behält den vorigen Stand), und
/// Hardware-Bereiche, die sich nicht lesen ließen, stehen in <see cref="HardwareInfo.Failed"/> und werden übernommen.
/// </summary>
internal sealed class MacInventoryCollector(string hostName)
{
    private static readonly TimeSpan ProfilerTimeout = TimeSpan.FromMinutes(3);

    public InventoryData Collect()
    {
        var data = new InventoryData { Platform = AgentPlatforms.MacOS, ComputerName = hostName, Manufacturer = "Apple Inc." };
        var hardware = new HardwareInfo();
        data.Hardware = hardware;
        data.MacOs = new MacOsInfo();

        CollectSystem(data, hardware);
        // Ohne Programmliste meldete der Scan alle Programme als entfernt: dann lieber gar kein Ergebnis.
        // Volle Detailstufe: erst sie nennt Herkunft und Architektur jedes Programms.
        using (var apps = MacParsers.Json(Shell.Run("/usr/sbin/system_profiler", ["-json", "SPApplicationsDataType"], ProfilerTimeout)))
            data.Software = MacParsers.Applications(apps ?? throw new InvalidOperationException("system_profiler SPApplicationsDataType failed"));
        CollectVolumes(data);
        CollectNetwork(data);
        // Fehlt die Liste (null), behält der Server die vorige.
        Section(data, "Packages", () => data.Apps = CollectPackages());
        Section(data, "Hardware", () => CollectHardware(hardware),
            () => hardware.Failed.AddRange([HardwareAreas.Graphics, HardwareAreas.Monitors, HardwareAreas.Memory, HardwareAreas.Disks, HardwareAreas.Battery, HardwareAreas.Printers]));
        Section(data, "FileVault", () =>
        {
            var (on, percent) = MacParsers.FileVault(Shell.Run("/usr/bin/fdesetup", "status"));
            if (on is null)
                throw new InvalidOperationException("fdesetup status failed");
            hardware.Volumes.Add(new VolumeEncryptionItem { Drive = "/", Protected = on, EncryptedPercent = percent, Method = "FileVault" });
        }, () => hardware.Failed.Add(HardwareAreas.Volumes));
        CollectUsers(data);
        Section(data, "Security", () => CollectSecurity(data.MacOs));
        return data;
    }

    /// <param name="failed">Vermerkt, welche Bereiche der Server vom vorigen Scan übernehmen soll.</param>
    private static void Section(InventoryData data, string name, Action collect, Action? failed = null)
    {
        try
        {
            collect();
        }
        catch (Exception ex)
        {
            data.Warnings.Add($"{name}: {ex.Message}");
            failed?.Invoke();
        }
    }

    private void CollectSystem(InventoryData data, HardwareInfo hardware)
    {
        (data.OsName, data.OsVersion) = MacParsers.Os(Shell.Run("/usr/bin/sw_vers"));
        if (data.OsName is null)
            throw new InvalidOperationException("sw_vers failed");
        data.OsArchitecture = Shell.Run("/usr/bin/uname", "-m")?.Trim() switch
        {
            "arm64" => "ARM 64-bit",
            "x86_64" => "64-bit",
            var other => other,
        };
        data.LastBootUtc = BootTimeUtc();
        // Ende des Einrichtungsassistenten: so alt ist die Installation.
        if (File.Exists("/var/db/.AppleSetupDone"))
            data.OsInstalledUtc = File.GetCreationTimeUtc("/var/db/.AppleSetupDone");
        data.CpuCores = Sysctl("hw.physicalcpu") is { } cores && int.TryParse(cores, out var c) ? c : Environment.ProcessorCount;
        data.CpuLogicalProcessors = Sysctl("hw.logicalcpu") is { } logical && int.TryParse(logical, out var l) ? l : Environment.ProcessorCount;
        data.MemoryBytes = Sysctl("hw.memsize") is { } memory && long.TryParse(memory, out var m) ? m : 0;

        using var profile = MacParsers.Json(Shell.Run("/usr/sbin/system_profiler", ["-json", "SPHardwareDataType"], ProfilerTimeout));
        if (MacParsers.Hardware(profile) is { } overview)
        {
            var marketing = MacParsers.ProductName(Shell.Run("/usr/sbin/ioreg", "-arc", "IOPlatformDevice", "-k", "product-name"));
            data.Model = (marketing ?? overview.ModelName) is { } name
                ? overview.ModelIdentifier is { } id ? $"{name} ({id})" : name
                : overview.ModelIdentifier;
            data.SerialNumber = overview.SerialNumber;
            data.CpuName = overview.Cpu;
            data.BiosVersion = overview.Firmware;
            hardware.ChassisType = MacParsers.ChassisType(overview.ModelName, overview.ModelIdentifier);
            data.Licensing = new LicensingInfo { BiosUuid = overview.HardwareUuid, CpuSockets = 1, CpuCoresPerSocket = data.CpuCores };
        }
        else
        {
            data.SerialNumber = MacParsers.IoregValue(Shell.Run("/usr/sbin/ioreg", "-rd1", "-c", "IOPlatformExpertDevice"), "IOPlatformSerialNumber");
        }

        // Wer an der Konsole angemeldet ist; im Anmeldefenster gehört sie root.
        var console = Shell.Run("/usr/bin/stat", "-f", "%Su", "/dev/console")?.Trim();
        if (console is { Length: > 0 } and not "root" and not "_mbsetupuser")
        {
            data.LoggedOnUser = console;
            data.InteractiveUsers = [console];
        }
        else
        {
            data.InteractiveUsers = [];
        }
        // An ein Active Directory gebunden: dessen Domäne.
        data.Domain = MacParsers.KeyValues(Shell.Run("/usr/sbin/dsconfigad", "-show"), '=').GetValueOrDefault("Active Directory Domain");
    }

    internal static DateTime? BootTimeUtc() => MacParsers.BootTime(Shell.Run("/usr/sbin/sysctl", "-n", "kern.boottime"));

    private static string? Sysctl(string name) => Shell.Run("/usr/sbin/sysctl", "-n", name)?.Trim();

    /// <summary>Das Startvolume und eingehängte Volumes, ohne die Hilfsvolumes des Systems.</summary>
    private static void CollectVolumes(InventoryData data)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            var path = drive.Name.TrimEnd('/');
            var isRoot = drive.Name == "/";
            if (!isRoot && !path.StartsWith("/Volumes/", StringComparison.Ordinal))
                continue;
            if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.Ram or DriveType.CDRom || drive.TotalSize == 0)
                continue;
            var info = Shell.Run("/usr/sbin/diskutil", "info", drive.Name);
            // Eingehängte Disk-Images (.dmg, meist Installer) sind keine Datenträger des Macs.
            if (MacParsers.DiskutilValue(info, "Protocol") == "Disk Image")
                continue;
            data.Disks.Add(new DiskItem
            {
                Drive = isRoot ? "/" : path,
                Label = MacParsers.DiskutilValue(info, "Volume Name") ?? Path.GetFileName(path),
                FileSystem = MacParsers.DiskutilValue(info, "File System Personality") ?? drive.DriveFormat,
                SizeBytes = drive.TotalSize,
                FreeBytes = drive.AvailableFreeSpace,
            });
        }
    }

    private static void CollectNetwork(InventoryData data)
    {
        if (MacParsers.DefaultInterface(Shell.Run("/sbin/route", "-n", "get", "default")) is { } primary)
            data.PrimaryIpAddress = Shell.Run("/usr/sbin/ipconfig", "getifaddr", primary)?.Trim() is { Length: > 0 } ip ? ip : null;
        var ports = MacParsers.HardwarePorts(Shell.Run("/usr/sbin/networksetup", "-listallhardwareports"));
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || adapter.OperationalStatus != OperationalStatus.Up)
                continue;
            var properties = adapter.GetIPProperties();
            var ipv4 = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
            // Nur Anschlüsse mit Adresse; Hilfsschnittstellen (awdl, llw, utun, bridge ohne Adresse) fallen so weg.
            if (ipv4.Count == 0)
                continue;
            var mac = adapter.GetPhysicalAddress().GetAddressBytes();
            var item = new NetworkAdapterItem
            {
                Name = ports.TryGetValue(adapter.Name, out var port) ? $"{port} ({adapter.Name})" : adapter.Name,
                MacAddress = mac.Length == 6 ? string.Join(":", mac.Select(b => b.ToString("X2"))) : null,
                IpAddresses = properties.UnicastAddresses.Where(a => !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList(),
                SubnetMasks = ipv4.Select(a => a.IPv4Mask?.ToString() ?? "").ToList(),
                DnsServers = properties.DnsAddresses.Select(a => a.ToString()).ToList(),
                // Hat der Anschluss eine DHCP-Zuteilung, nennt ipconfig das Paket des Servers.
                DhcpEnabled = !string.IsNullOrWhiteSpace(Shell.Run("/usr/sbin/ipconfig", "getpacket", adapter.Name)),
            };
            try
            {
                item.Gateways = properties.GatewayAddresses.Select(g => g.Address.ToString()).Where(a => a != "0.0.0.0").ToList();
            }
            catch (PlatformNotSupportedException)
            {
            }
            data.NetworkAdapters.Add(item);
        }
    }

    private static void CollectHardware(HardwareInfo hardware)
    {
        using var profile = MacParsers.Json(Shell.Run("/usr/sbin/system_profiler",
            ["-json", "SPDisplaysDataType", "SPMemoryDataType", "SPNVMeDataType", "SPSerialATADataType", "SPPowerDataType", "SPPrintersDataType"], ProfilerTimeout));
        if (profile is null)
            throw new InvalidOperationException("system_profiler failed");
        (hardware.Gpus, hardware.Monitors) = MacParsers.Displays(profile);
        (hardware.MemorySlots, hardware.MemoryModules) = MacParsers.Memory(profile);
        hardware.PhysicalDisks = MacParsers.Disks(profile);
        (hardware.BatteryPresent, hardware.BatteryHealthPercent, hardware.BatteryCycleCount) = MacParsers.Battery(profile);
        hardware.Printers = MacParsers.Printers(profile);
    }

    /// <summary>Installer-Pakete (ohne die von Apple) und Homebrew-Formeln und -Casks, gelesen aus den Ordnern statt mit brew.</summary>
    private static List<AppItem> CollectPackages()
    {
        var ids = Shell.Run("/usr/sbin/pkgutil", "--pkgs") ?? throw new InvalidOperationException("pkgutil failed");
        var receipts = ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => !id.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase))
            .Select(id => MacParsers.Receipt(Shell.Run("/usr/sbin/pkgutil", "--pkg-info", id)))
            .OfType<MacParsers.PackageReceipt>();
        var packages = MacParsers.Packages(receipts);
        foreach (var prefix in new[] { "/opt/homebrew", "/usr/local" })
        {
            foreach (var (folder, kind) in new[] { ("Cellar", AppKinds.HomebrewFormula), ("Caskroom", AppKinds.HomebrewCask) })
            {
                var root = Path.Combine(prefix, folder);
                if (!Directory.Exists(root))
                    continue;
                foreach (var formula in Directory.GetDirectories(root))
                {
                    // Je Formel ein Unterordner pro installierter Version; die höchste zählt.
                    var version = Directory.GetDirectories(formula).Select(Path.GetFileName).Where(v => !string.IsNullOrEmpty(v) && v[0] != '.')
                        .OrderBy(v => v, Comparer<string?>.Create(VersionComparer.Compare)).LastOrDefault();
                    packages.Add(new AppItem { Name = Path.GetFileName(formula), Version = version, Publisher = "Homebrew", Kind = kind });
                }
            }
        }
        return packages;
    }

    private static void CollectUsers(InventoryData data)
    {
        var users = Shell.Run("/usr/bin/dscl", ".", "-list", "/Users", "UniqueID") ?? throw new InvalidOperationException("dscl failed");
        var now = DateTime.UtcNow;
        foreach (var name in MacParsers.Users(users))
        {
            var record = Shell.Run("/usr/bin/dscl", ".", "-read", "/Users/" + name, "RealName", "AuthenticationAuthority");
            var passwordSet = MacParsers.PasswordLastSet(Shell.Run("/usr/bin/dscl", ".", "-readpl", "/Users/" + name, "accountPolicyData", "passwordLastSetTime"));
            data.LocalUsers.Add(new LocalUserItem
            {
                Name = name,
                FullName = string.Join(" ", MacParsers.DsclValues(record, "RealName")) is { Length: > 0 } full ? full : null,
                Disabled = MacParsers.DsclValues(record, "AuthenticationAuthority").Any(a => a.Contains("DisabledUser", StringComparison.Ordinal)),
                LastLogonUtc = MacParsers.LastLogin(Shell.Run("/usr/bin/last", "-1", name), DateTime.Now, TimeZoneInfo.Local),
                PasswordAgeDays = passwordSet is { } set ? (int)(now - set).TotalDays : null,
            });
        }
        foreach (var member in MacParsers.DsclValues(Shell.Run("/usr/bin/dscl", ".", "-read", "/Groups/admin", "GroupMembership"), "GroupMembership"))
        {
            // Systemkonten (root, _mbsetupuser für den Einrichtungsassistenten) sind keine Administratoren im Alltag.
            if (member != "root" && !member.StartsWith('_'))
                data.LocalAdmins.Add(new LocalAdminItem { Name = member });
        }
    }

    private static void CollectSecurity(MacOsInfo mac)
    {
        const string firewall = "/usr/libexec/ApplicationFirewall/socketfilterfw";
        mac.FirewallEnabled = MacParsers.Switch(Shell.Run(firewall, "--getglobalstate"));
        mac.FirewallStealth = MacParsers.Switch(Shell.Run(firewall, "--getstealthmode"));
        mac.GatekeeperEnabled = MacParsers.Switch(Shell.Run("/usr/sbin/spctl", "--status"));
        mac.SipEnabled = MacParsers.Switch(Shell.Run("/usr/bin/csrutil", "status"));
        // Per Profil verwaltete Einstellung vor der lokalen; fehlt beides, gilt die Voreinstellung von macOS (an).
        mac.AutomaticUpdateCheck =
            MacParsers.Defaults(Shell.Run("/usr/bin/defaults", "read", "/Library/Managed Preferences/com.apple.SoftwareUpdate", "AutomaticCheckEnabled"))
            ?? MacParsers.Defaults(Shell.Run("/usr/bin/defaults", "read", "/Library/Preferences/com.apple.SoftwareUpdate", "AutomaticCheckEnabled"))
            ?? true;
    }

    /// <summary>Für <c>--collect</c>: läuft der Agent auf dem Gerät, das er erfasst?</summary>
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}
