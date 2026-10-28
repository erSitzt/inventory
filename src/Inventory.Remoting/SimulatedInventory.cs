using Inventory.Contracts;

namespace Inventory.Remoting;

/// <summary>Erzeugt je Rechnername gleichbleibende, plausible Inventardaten für den Simulationsmodus.</summary>
internal static class SimulatedInventory
{
    private static readonly (string Manufacturer, string Model)[] Models =
    [
        ("HP", "HP EliteDesk 800 G6"), ("HP", "HP EliteBook 840 G8"), ("Dell Inc.", "OptiPlex 7090"),
        ("Dell Inc.", "Latitude 5520"), ("LENOVO", "ThinkPad T14 Gen 3"), ("LENOVO", "ThinkCentre M70q"),
    ];

    private static readonly (string Release, string Version, string Name)[] Systems =
    [
        ("22H2", "10.0.19045.5011", "Microsoft Windows 10 Pro"), ("23H2", "10.0.22631.4317", "Microsoft Windows 11 Pro"),
        ("24H2", "10.0.26100.2033", "Microsoft Windows 11 Enterprise"), ("25H2", "10.0.26200.9457", "Microsoft Windows 11 Enterprise"),
    ];

    private static readonly (string Name, string Publisher, string[] Versions)[] Catalog =
    [
        ("7-Zip 24.08 (x64)", "Igor Pavlov", ["23.01", "24.08"]),
        ("Google Chrome", "Google LLC", ["139.0.7258.66", "141.0.7390.55", "141.0.7390.108"]),
        ("Mozilla Firefox (x64 de)", "Mozilla", ["128.3.0", "131.0.2"]),
        ("Microsoft 365 Apps for enterprise - de-de", "Microsoft Corporation", ["16.0.17928.20156", "16.0.18129.20116"]),
        ("Adobe Acrobat Reader DC - Deutsch", "Adobe Inc.", ["24.002.20933", "24.003.20180"]),
        ("Notepad++ (64-bit x64)", "Notepad++ Team", ["8.6.9", "8.7"]),
        ("VLC media player", "VideoLAN", ["3.0.20", "3.0.21"]),
        ("Microsoft Teams", "Microsoft", ["24243.1309.3132.617"]),
        ("TeamViewer", "TeamViewer", ["15.57.5", "15.58.4"]),
        ("Java 8 Update 421 (64-bit)", "Oracle Corporation", ["8.0.4210.9"]),
    ];

    public static InventoryData Create(string host)
    {
        var random = new Random((int)StableHash(host));
        var (manufacturer, model) = Models[random.Next(Models.Length)];
        var system = Systems[random.Next(Systems.Length)];
        var memoryGb = new[] { 8, 16, 16, 32 }[random.Next(4)];
        var diskGb = new[] { 256, 512, 512, 1024 }[random.Next(4)];

        var data = new InventoryData
        {
            ComputerName = host.Split('.')[0].ToUpperInvariant(),
            Domain = "corp.example",
            Manufacturer = manufacturer,
            Model = model,
            SerialNumber = $"SIM{StableHash(host) % 10_000_000:D7}",
            BiosVersion = $"1.{random.Next(5, 30)}.0",
            OsName = system.Name,
            OsVersion = system.Version,
            OsRelease = system.Release,
            OsArchitecture = "64-Bit",
            OsInstalledUtc = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(random.Next(900)),
            LastBootUtc = DateTime.UtcNow.AddHours(-random.Next(1, 400)),
            CpuName = "Intel(R) Core(TM) i5-10500 CPU @ 3.10GHz",
            CpuCores = 6,
            CpuLogicalProcessors = 12,
            MemoryBytes = memoryGb * 1024L * 1024 * 1024,
            LoggedOnUser = random.Next(4) == 0 ? null : $"CORP\\benutzer{random.Next(100, 999)}",
        };

        foreach (var (name, publisher, versions) in Catalog)
        {
            if (random.Next(10) < 7)
                data.Software.Add(new SoftwareItem { Name = name, Publisher = publisher, Version = versions[random.Next(versions.Length)], InstallDate = "20250314", Is64Bit = true });
        }
        data.Hotfixes.Add(new HotfixItem { Id = "KB5044284", Description = "Security Update", InstalledOn = "10/8/2025" });
        data.Hotfixes.Add(new HotfixItem { Id = "KB5043080", Description = "Update", InstalledOn = "9/10/2025" });
        data.Disks.Add(new DiskItem { Drive = "C:", Label = "Windows", FileSystem = "NTFS", SizeBytes = diskGb * 1_000_000_000L, FreeBytes = diskGb * 1_000_000_000L * random.Next(5, 80) / 100 });
        // Feste Netze wie in einer kleinen Firma: Hauptsitz, Server, Filiale – damit die Netzwerkkarte etwas zu zeigen hat.
        var network = host.StartsWith("SRV", StringComparison.OrdinalIgnoreCase) ? 1 : random.Next(4) == 0 ? 20 : 0;
        var subnet = network == 20 ? "10.20.0" : $"10.0.{network}";
        data.NetworkAdapters.Add(new NetworkAdapterItem
        {
            Name = "Intel(R) Ethernet Connection I219-LM", MacAddress = $"00:1A:2B:{random.Next(256):X2}:{random.Next(256):X2}:{random.Next(256):X2}",
            IpAddresses = [$"{subnet}.{random.Next(100, 250)}"], SubnetMasks = ["255.255.255.0"], Gateways = [$"{subnet}.1"], DnsServers = ["10.0.1.10", "10.0.1.11"], DhcpEnabled = network != 1,
        });
        data.Services.Add(new ServiceItem { Name = "Spooler", DisplayName = "Druckwarteschlange", State = "Running", StartMode = "Auto", Account = "LocalSystem" });
        data.Services.Add(new ServiceItem { Name = "wuauserv", DisplayName = "Windows Update", State = "Stopped", StartMode = "Manual", Account = "LocalSystem" });
        data.Services.Add(new ServiceItem { Name = "WinRM", DisplayName = "Windows-Remoteverwaltung", State = random.Next(2) == 0 ? "Running" : "Stopped", StartMode = "Manual", Account = "NT AUTHORITY\\NetworkService" });
        var pending = random.Next(4) == 0
            ? new List<string> { "2026-10 Cumulative Update for Windows 11 (KB5066835)", "Update for Microsoft Defender Antivirus antimalware platform" }
            : new List<string>();
        data.Security = new SecurityStatus
        {
            FirewallDomain = random.Next(6) != 0, FirewallPrivate = true, FirewallPublic = true,
            DefenderEnabled = true, DefenderRealTime = random.Next(8) != 0, DefenderTamperProtected = true,
            DefenderSignatureVersion = $"1.459.{random.Next(100, 900)}.0", DefenderSignatureUpdatedUtc = DateTime.UtcNow.AddHours(-random.Next(2, 90)),
            AntivirusProducts = ["Windows Defender"], AntivirusProductDetails = [new AntivirusProductItem { Name = "Windows Defender", Enabled = true, UpToDate = true }], DefenderMode = "Normal",
            UpdateLastSearchUtc = DateTime.UtcNow.AddHours(-random.Next(1, 30)), UpdateLastInstallUtc = DateTime.UtcNow.AddDays(-random.Next(1, 60)),
            UpdateRebootRequired = pending.Count > 0 && random.Next(2) == 0, UpdatePending = pending,
            UpdatePendingDetails = pending.Select(title => new UpdateItem
            {
                Title = title, KbIds = Inventory.Core.PendingUpdates.KbIdsFromTitle(title), IsDownloaded = false, SizeBytes = 0,
                Classification = title.Contains("Cumulative") ? "Security Updates" : "Definition Updates", Severity = title.Contains("Cumulative") ? "Critical" : null,
            }).ToList(), UpdateServer = "http://wsus.corp.example:8530",
        };
        data.RemoteAccess = new RemoteAccessInfo
        {
            SessionId = data.LoggedOnUser is null ? null : 1,
            TeamViewerId = random.Next(3) == 0 ? null : random.Next(100_000_000, 999_999_999).ToString(),
            AnyDeskId = random.Next(4) == 0 ? random.Next(100_000_000, 999_999_999).ToString() : null,
        };
        // Ohne den Zufallsgenerator: die übrigen simulierten Werte sollen sich dadurch nicht verschieben.
        data.Apps =
        [
            new AppItem { Name = "Microsoft.WindowsCalculator", Version = "11.2405.2.0", Publisher = "Microsoft Corporation", Architecture = "x64", Provisioned = true },
            new AppItem { Name = "Microsoft.BingWeather", Version = "4.53.52331.0", Publisher = "Microsoft Corporation", Architecture = "x64", Provisioned = true },
            new AppItem { Name = "MSTeams", DisplayName = "Microsoft Teams", Version = "24137.2203.2869.6690", Publisher = "Microsoft", Architecture = "x64" },
        ];
        if (host.Length % 2 == 0)
            data.Apps.Add(new AppItem { Name = "Microsoft.XboxGamingOverlay", DisplayName = "Game Bar", Version = "7.124.5142.0", Publisher = "Microsoft Corporation", Architecture = "x64", Provisioned = true });
        var laptop = model.Contains("Book") || model.Contains("Latitude") || model.Contains("ThinkPad");
        var modules = memoryGb >= 16 ? 2 : 1;
        data.Hardware = new HardwareInfo
        {
            ChassisType = laptop ? 10 : 3,
            FirmwareType = "UEFI",
            SecureBoot = random.Next(10) != 0,
            BiosDateUtc = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(random.Next(1000)),
            TpmPresent = true,
            TpmVersion = "2.0",
            TpmReady = random.Next(12) != 0,
            Gpus = [new GpuItem { Name = "Intel(R) UHD Graphics 630", DriverVersion = "31.0.101.2125" }],
            MemorySlots = laptop ? 2 : 4,
            MemoryModules = Enumerable.Range(1, modules).Select(i => new MemoryModuleItem
            {
                Slot = $"DIMM {i}", Manufacturer = "Samsung", PartNumber = "M471A1K43DB1-CWE", SerialNumber = $"{random.Next(0x10000000, 0x7FFFFFFF):X8}",
                CapacityBytes = memoryGb * 1024L * 1024 * 1024 / modules, SpeedMhz = 3200, Type = "DDR4",
            }).ToList(),
            PhysicalDisks =
            [
                new PhysicalDiskItem
                {
                    Model = $"SAMSUNG MZVLB{diskGb}HBJQ", SerialNumber = $"S4EW{random.Next(100000, 999999)}", MediaType = "SSD", BusType = "NVMe",
                    SizeBytes = diskGb * 1_000_000_000L, Health = random.Next(25) == 0 ? "Warning" : "Healthy",
                },
            ],
            Volumes = [new VolumeEncryptionItem { Drive = "C:", Protected = laptop ? random.Next(8) != 0 : random.Next(3) == 0, EncryptedPercent = 100, Method = "XTS-AES 256" }],
            Monitors = laptop
                ? [new MonitorItem { Manufacturer = "BOE", Model = "BOE0812", Year = 2021 }]
                : [new MonitorItem { Manufacturer = "DEL", Model = "DELL P2422H", SerialNumber = $"{random.Next(1000000, 9999999)}", Year = 2022 }],
            Printers = random.Next(3) == 0 ? [new PrinterItem { Name = "Etage-2-Laser", Driver = "HP Universal Printing PCL 6", Port = "10.0.9.21", Network = false }] : [],
            BatteryPresent = laptop,
            BatteryHealthPercent = laptop ? random.Next(55, 100) : null,
            BatteryCycleCount = laptop ? random.Next(20, 700) : null,
        };
        var adminActive = random.Next(5) == 0;
        var adminPasswordAge = random.Next(3, 900);
        data.Security.Posture = new SecurityPosture
        {
            RdpEnabled = random.Next(3) == 0, RdpNla = random.Next(10) != 0, UacEnabled = random.Next(20) != 0, UacLevel = 2,
            Smb1Enabled = random.Next(12) == 0, CredentialGuard = random.Next(2) == 0, MemoryIntegrity = random.Next(2) == 0,
            LsaProtection = random.Next(2) == 0, BuiltinAdminEnabled = adminActive, BuiltinAdminPasswordAgeDays = adminPasswordAge,
            WindowsActivated = random.Next(25) != 0,
        };
        data.LocalUsers.Add(new LocalUserItem
        {
            Name = "Administrator", Disabled = !adminActive, Description = "Vordefiniertes Konto", Sid = "S-1-5-21-1000000000-2000000000-3000000000-500",
            PasswordAgeDays = adminPasswordAge,
        });
        data.LocalUsers.Add(new LocalUserItem { Name = "Gast", Disabled = true });
        data.LocalAdmins.Add(new LocalAdminItem { Name = $"{data.ComputerName}\\Administrator" });
        data.LocalAdmins.Add(new LocalAdminItem { Name = "CORP\\Domain Admins", IsGroup = true });
        if (random.Next(3) == 0)
        {
            data.LocalUsers.Add(new LocalUserItem { Name = "support", Description = "Lokales Supportkonto" });
            data.LocalAdmins.Add(new LocalAdminItem { Name = $"{data.ComputerName}\\support" });
        }
        data.Licensing = Licensing(data, random);
        return data;
    }

    /// <summary>Server-VMs der Simulation und auf welchem der beiden Hyper-V-Hosts sie laufen.</summary>
    private static readonly string[] ServerVms = ["SRV-DC01", "SRV-FILE01", "SRV-SQL01", "SRV-SQL02", "SRV-APP01", "SRV-APP02", "SRV-RDS01"];
    private static readonly string[] HyperVHosts = ["HV-01", "HV-02"];

    private static string HostOf(string vm) => HyperVHosts[StableHash(vm) % (uint)HyperVHosts.Length];

    /// <summary>
    /// UUID, die ein VMware-vCenter für die VM führt; Windows im Gast meldet sie mit vertauschter Bytefolge in den ersten
    /// drei Feldern (siehe <see cref="Core.Model.BiosUuids"/>).
    /// </summary>
    public static string VmwareUuid(string name) => Core.Model.BiosUuids.Swapped(Uuid(name)).ToLowerInvariant();

    /// <summary>UUID einer Xen-VM; der Gast meldet sie unverändert als SMBIOS-UUID.</summary>
    public static string XenUuid(string name) => Uuid(name).ToLowerInvariant();

    /// <summary>Gleichbleibende SMBIOS-UUID je Name: Host und Gast melden so dieselbe.</summary>
    private static string Uuid(string name) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToUpperInvariant()))).ToString("D").ToUpperInvariant();

    /// <summary>
    /// Lizenzangaben: HV-* sind physische Hyper-V-Hosts im Cluster, SRV-* Server-VMs darauf (SRV-SQL* mit SQL Server),
    /// alles andere Arbeitsplätze mit OEM- oder Volumenlizenz.
    /// </summary>
    private static LicensingInfo Licensing(InventoryData data, Random random)
    {
        var name = data.ComputerName ?? "";
        if (HyperVHosts.Contains(name))
        {
            data.Manufacturer = "HPE";
            data.Model = "ProLiant DL380 Gen10 Plus";
            data.OsName = "Microsoft Windows Server 2022 Datacenter";
            data.OsVersion = "10.0.20348.2700";
            data.OsRelease = "21H2";
            data.CpuName = "Intel(R) Xeon(R) Gold 6326 CPU @ 2.90GHz";
            data.CpuCores = 32;
            data.CpuLogicalProcessors = 64;
            return new LicensingInfo
            {
                CpuSockets = 2, CpuCoresPerSocket = 16, BiosUuid = Uuid(name), HypervisorPresent = true, HyperVGuest = false,
                OsSku = 8, OsProductType = 3, OsEdition = "ServerDatacenter", LicenseChannel = LicenseChannels.VolumeMak,
                ServerFeatures = [new ServerFeatureItem { Id = 20, Name = "Hyper-V" }, new ServerFeatureItem { Id = 33, Name = "Failover Clustering" }],
                SqlInstances = [], ClusterName = "HVCLUSTER01", HyperVHost = true,
                VirtualMachines = ServerVms.Where(vm => HostOf(vm) == name).Select(vm => new HostedVmItem
                {
                    Name = vm, BiosUuid = "{" + Uuid(vm) + "}", VirtualCpus = vm.StartsWith("SRV-SQL", StringComparison.Ordinal) ? 8 : 4, Running = true,
                }).ToList(),
            };
        }
        if (ServerVms.Contains(name))
        {
            data.Manufacturer = "Microsoft Corporation";
            data.Model = "Virtual Machine";
            data.OsName = "Microsoft Windows Server 2022 Standard";
            data.OsVersion = "10.0.20348.2700";
            data.OsRelease = "21H2";
            data.CpuName = "Intel(R) Xeon(R) Gold 6326 CPU @ 2.90GHz";
            var cores = name.StartsWith("SRV-SQL", StringComparison.Ordinal) ? 8 : 4;
            data.CpuCores = cores;
            data.CpuLogicalProcessors = cores;
            var features = new List<ServerFeatureItem>();
            if (name == "SRV-RDS01")
                features.Add(new ServerFeatureItem { Id = 130, Name = "Remote Desktop Session Host" });
            return new LicensingInfo
            {
                CpuSockets = 1, CpuCoresPerSocket = cores, BiosUuid = Uuid(name), HypervisorPresent = true, HyperVGuest = true,
                ReportedHostName = HostOf(name).ToLowerInvariant() + ".corp.example", ClusterName = "", HyperVHost = false,
                OsSku = 7, OsProductType = name == "SRV-DC01" ? 2 : 3, OsEdition = "ServerStandard", LicenseChannel = LicenseChannels.VolumeGvlk,
                ServerFeatures = features,
                SqlInstances = name switch
                {
                    "SRV-SQL01" => [new SqlInstanceItem { Name = "MSSQLSERVER", InstanceId = "MSSQL16.MSSQLSERVER", Edition = "Standard Edition (64-bit)", Version = "16.0.4135.4", PatchLevel = "16.0.4135.4" }],
                    "SRV-SQL02" =>
                    [
                        new SqlInstanceItem { Name = "MSSQLSERVER", InstanceId = "MSSQL16.MSSQLSERVER", Edition = "Enterprise Edition: Core-based Licensing (64-bit)", Version = "16.0.4135.4", PatchLevel = "16.0.4135.4" },
                        new SqlInstanceItem { Name = "DEV", InstanceId = "MSSQL16.DEV", Edition = "Developer Edition (64-bit)", Version = "16.0.4135.4", PatchLevel = "16.0.4135.4" },
                    ],
                    _ => [],
                },
            };
        }
        if (SimulatedXcpNgSource.Guests.Contains(name))
        {
            data.Manufacturer = "Xen";
            data.Model = "HVM domU";
            data.OsName = "Microsoft Windows Server 2022 Standard";
            data.OsVersion = "10.0.20348.2700";
            data.OsRelease = "21H2";
            var xenCpus = name.Contains("SQL", StringComparison.Ordinal) ? 8 : 4;
            data.CpuCores = xenCpus;
            data.CpuLogicalProcessors = xenCpus;
            return new LicensingInfo
            {
                CpuSockets = 1, CpuCoresPerSocket = xenCpus, BiosUuid = Uuid(name), HypervisorPresent = true, HyperVGuest = false,
                OsSku = 7, OsProductType = 3, OsEdition = "ServerStandard", LicenseChannel = LicenseChannels.VolumeGvlk,
                ServerFeatures = [], SqlInstances = [], ClusterName = "", HyperVHost = false,
            };
        }
        if (SimulatedHypervisorSource.Guests.Contains(name))
        {
            data.Manufacturer = "VMware, Inc.";
            data.Model = "VMware7,1";
            data.OsName = "Microsoft Windows Server 2022 Standard";
            data.OsVersion = "10.0.20348.2700";
            data.OsRelease = "21H2";
            var vcpus = name.StartsWith("VM-SQL", StringComparison.Ordinal) ? 8 : 4;
            data.CpuCores = vcpus;
            data.CpuLogicalProcessors = vcpus;
            return new LicensingInfo
            {
                CpuSockets = vcpus / 2, CpuCoresPerSocket = 2, BiosUuid = Uuid(name), HypervisorPresent = true, HyperVGuest = false,
                OsSku = 7, OsProductType = 3, OsEdition = "ServerStandard", LicenseChannel = LicenseChannels.VolumeGvlk,
                ServerFeatures = name == "VM-RDS01" ? [new ServerFeatureItem { Id = 130, Name = "Remote Desktop Session Host" }] : [],
                SqlInstances = name == "VM-SQL01"
                    ? [new SqlInstanceItem { Name = "MSSQLSERVER", InstanceId = "MSSQL16.MSSQLSERVER", Edition = "Standard Edition (64-bit)", Version = "16.0.4135.4" }]
                    : [],
                ClusterName = "", HyperVHost = false,
            };
        }
        var pro = (data.OsName ?? "").Contains("Pro", StringComparison.Ordinal);
        return new LicensingInfo
        {
            CpuSockets = 1, CpuCoresPerSocket = data.CpuCores, BiosUuid = Uuid(name), HypervisorPresent = false, HyperVGuest = false,
            OsSku = pro ? 48 : 4, OsProductType = 1, OsEdition = pro ? "Professional" : "Enterprise",
            LicenseChannel = pro ? (random.Next(4) == 0 ? LicenseChannels.Retail : LicenseChannels.OemDm) : random.Next(6) == 0 ? LicenseChannels.VolumeGvlk : LicenseChannels.VolumeMak,
            SqlInstances = [], ClusterName = "", HyperVHost = false,
        };
    }

    internal static uint StableHash(string value)
    {
        // FNV-1a; string.GetHashCode ist pro Prozess zufällig und damit nicht stabil.
        var hash = 2166136261;
        foreach (var c in value.ToUpperInvariant())
            hash = (hash ^ c) * 16777619;
        return hash;
    }
}
