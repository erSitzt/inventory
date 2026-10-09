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
        data.NetworkAdapters.Add(new NetworkAdapterItem
        {
            Name = "Intel(R) Ethernet Connection I219-LM", MacAddress = $"00:1A:2B:{random.Next(256):X2}:{random.Next(256):X2}:{random.Next(256):X2}",
            IpAddresses = [$"10.0.{random.Next(1, 250)}.{random.Next(1, 250)}"], Gateways = ["10.0.0.1"], DnsServers = ["10.0.0.10", "10.0.0.11"], DhcpEnabled = true,
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
            AntivirusProducts = ["Windows Defender"],
            UpdateLastSearchUtc = DateTime.UtcNow.AddHours(-random.Next(1, 30)), UpdateLastInstallUtc = DateTime.UtcNow.AddDays(-random.Next(1, 60)),
            UpdateRebootRequired = pending.Count > 0 && random.Next(2) == 0, UpdatePending = pending, UpdateServer = "http://wsus.corp.example:8530",
        };
        data.RemoteAccess = new RemoteAccessInfo
        {
            SessionId = data.LoggedOnUser is null ? null : 1,
            TeamViewerId = random.Next(3) == 0 ? null : random.Next(100_000_000, 999_999_999).ToString(),
            AnyDeskId = random.Next(4) == 0 ? random.Next(100_000_000, 999_999_999).ToString() : null,
        };
        data.LocalUsers.Add(new LocalUserItem { Name = "Administrator", Disabled = true, Description = "Vordefiniertes Konto" });
        data.LocalUsers.Add(new LocalUserItem { Name = "Gast", Disabled = true });
        data.LocalAdmins.Add(new LocalAdminItem { Name = $"{data.ComputerName}\\Administrator" });
        data.LocalAdmins.Add(new LocalAdminItem { Name = "CORP\\Domain Admins", IsGroup = true });
        if (random.Next(3) == 0)
        {
            data.LocalUsers.Add(new LocalUserItem { Name = "support", Description = "Lokales Supportkonto" });
            data.LocalAdmins.Add(new LocalAdminItem { Name = $"{data.ComputerName}\\support" });
        }
        return data;
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
