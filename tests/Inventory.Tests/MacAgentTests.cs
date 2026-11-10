using Inventory.Agent.MacOS;
using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>Macs mit Agent: Ausgaben der macOS-Programme lesen, Anmeldung, Speicherung und was für Macs (noch) nicht geht.</summary>
public class MacAgentTests
{
    // Ausgaben wie auf einem MacBook Pro mit Apple Silicon (gekürzt).
    private const string SwVers = "ProductName:\t\tmacOS\nProductVersion:\t\t15.1\nBuildVersion:\t\t24B83\n";

    private const string HardwareJson = """
        {"SPHardwareDataType":[{"_name":"hardware_overview","boot_rom_version":"11881.41.5","chip_type":"Apple M3 Pro","machine_model":"Mac15,6",
          "machine_name":"MacBook Pro","number_processors":"proc 11:5:6","physical_memory":"18 GB","platform_UUID":"0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9",
          "serial_number":"FVFXC0ABCD12"}]}
        """;

    private const string IntelHardwareJson = """
        {"SPHardwareDataType":[{"_name":"hardware_overview","cpu_type":"Quad-Core Intel Core i5","current_processor_speed":"2 GHz","machine_model":"MacBookPro16,2",
          "machine_name":"MacBook Pro","number_processors":4,"packages":1,"physical_memory":"16 GB","serial_number":"C02ABC123XYZ"}]}
        """;

    private const string ApplicationsJson = """
        {"SPApplicationsDataType":[
          {"_name":"Google Chrome","arch_kind":"arch_arm_i64","lastModified":"2024-10-29T09:12:33Z","obtained_from":"identified_developer",
           "path":"/Applications/Google Chrome.app","signed_by":["Developer ID Application: Google LLC (EQHXZ8M8AV)","Developer ID Certification Authority","Apple Root CA"],"version":"130.0.6723.92"},
          {"_name":"Safari","obtained_from":"apple","path":"/Applications/Safari.app","version":"18.1","lastModified":"2024-10-01T08:00:00Z"},
          {"_name":"Xcode Helper","obtained_from":"apple","path":"/Applications/Xcode.app/Contents/Applications/Helper.app","version":"1.0"},
          {"_name":"Calculator","obtained_from":"apple","path":"/System/Applications/Calculator.app","version":"11.0"},
          {"_name":"Slack","obtained_from":"mac_app_store","path":"/Users/max/Applications/Slack.app","version":"4.41","signed_by":["Apple Mac OS Application Signing"]},
          {"_name":"Utility","path":"/Applications/Utilities/Disk Tool.app","version":"2.0","signed_by":["Developer ID Application: Tools & Co. KG (ABCDE12345)"]}
        ]}
        """;

    private const string ProfileJson = """
        {"SPDisplaysDataType":[{"_name":"Apple M3 Pro","sppci_model":"Apple M3 Pro","sppci_cores":"14","spdisplays_ndrvs":[
            {"_name":"Color LCD","spdisplays_connection_type":"spdisplays_internal","spdisplays_display_type":"spdisplays_built-in-liquid-retina-xdr"},
            {"_name":"DELL U2723QE","_spdisplays_display-serial-number":"ABC123","_spdisplays_display-year":"2023"}]}],
         "SPMemoryDataType":[{"SPMemoryDataType":"18 GB","dimm_manufacturer":"Micron","dimm_type":"LPDDR5"}],
         "SPNVMeDataType":[{"_name":"Apple SSD Controller","_items":[{"_name":"APPLE SSD AP0512Z","device_model":"APPLE SSD AP0512Z","device_serial":"0ba0123","size":"500,28 GB","size_in_bytes":500277790720,"smart_status":"Verified"}]}],
         "SPSerialATADataType":[],
         "SPPowerDataType":[{"_name":"spbattery_information","sppower_battery_health_info":{"sppower_battery_cycle_count":123,"sppower_battery_health":"Good","sppower_battery_health_maximum_capacity":"92%"}},
                            {"_name":"sppower_ac_charger_information"}],
         "SPPrintersDataType":[{"_name":"Buero HP","uri":"ipp://10.0.0.20/ipp/print","ppd":"HP LaserJet Pro","shared":"no"}]}
        """;

    private const string IntelMemoryJson = """
        {"SPMemoryDataType":[{"_name":"Memory Slots","_items":[
          {"_name":"BANK 0/ChannelA-DIMM0","dimm_size":"8 GB","dimm_speed":"2667 MHz","dimm_type":"DDR4","dimm_manufacturer":"0x80AD","dimm_part_number":"HMA81GS6CJR8N-VK"},
          {"_name":"BANK 2/ChannelB-DIMM0","dimm_size":"8 GB","dimm_speed":"2667 MHz","dimm_type":"DDR4"}]}]}
        """;

    [Fact]
    public void System_and_hardware_are_read_from_sw_vers_and_system_profiler()
    {
        Assert.Equal(("macOS Sequoia 15.1", "15.1 (24B83)"), MacParsers.Os(SwVers));
        Assert.Equal(("macOS 27.0", "27.0 (27A1)"), MacParsers.Os("ProductName: macOS\nProductVersion: 27.0\nBuildVersion: 27A1"));
        Assert.Equal((null, null), MacParsers.Os(""));

        using var apple = MacParsers.Json(HardwareJson);
        var hardware = MacParsers.Hardware(apple)!;
        Assert.Equal(("MacBook Pro", "Mac15,6", "FVFXC0ABCD12", "Apple M3 Pro", "11881.41.5"),
            (hardware.ModelName, hardware.ModelIdentifier, hardware.SerialNumber, hardware.Cpu, hardware.Firmware));
        using var intel = MacParsers.Json(IntelHardwareJson);
        Assert.Equal("Quad-Core Intel Core i5 @ 2 GHz", MacParsers.Hardware(intel)!.Cpu);
        Assert.Null(MacParsers.Hardware(MacParsers.Json("not json")));

        Assert.Equal((10, 13, 3), (MacParsers.ChassisType("MacBook Air", "Mac15,12"), MacParsers.ChassisType("iMac", "iMac21,1"),
            MacParsers.ChassisType("Mac mini", "Mac14,3")));
        Assert.Null(MacParsers.ChassisType("Apple Virtual Machine 1", "VirtualMac2,1"));
        // Virtuelle Macs erkennt die Geräteklasse am Modell.
        Assert.Equal(ChassisKind.VirtualMachine, Chassis.Kind(null, "Apple Virtual Machine 1 (VirtualMac2,1)"));

        var plist = "<plist><dict><key>product-name</key><data>TWFjQm9vayBQcm8gKDE0LWluY2gsIE5vdiAyMDIzKQA=</data></dict></plist>";
        Assert.Equal("MacBook Pro (14-inch, Nov 2023)", MacParsers.ProductName(plist));
        Assert.Equal("C02ABC", MacParsers.IoregValue("  \"IOPlatformSerialNumber\" = \"C02ABC\"\n", "IOPlatformSerialNumber"));
        Assert.Equal(new DateTime(2024, 10, 31, 7, 33, 20, DateTimeKind.Utc), MacParsers.BootTime("{ sec = 1730360000, usec = 500000 } Thu Oct 31 08:33:20 2024"));
    }

    [Fact]
    public void Only_installed_applications_count_with_publisher_and_date()
    {
        using var json = MacParsers.Json(ApplicationsJson);
        var apps = MacParsers.Applications(json);
        Assert.Equal(["Google Chrome", "Safari", "Slack", "Utility"], apps.Select(a => a.Name));
        var chrome = apps[0];
        Assert.Equal(("130.0.6723.92", "Google LLC", "20241029", false), (chrome.Version, chrome.Publisher, chrome.InstallDate, chrome.PerUser));
        Assert.Equal("Apple Inc.", apps[1].Publisher);
        // App Store: Apple signiert, der Entwickler steht nicht in der Signatur.
        Assert.Equal((null, true), (apps[2].Publisher, apps[2].PerUser));
        Assert.Equal("Tools & Co. KG", apps[3].Publisher);
    }

    [Fact]
    public void Hardware_details_come_from_system_profiler()
    {
        using var profile = MacParsers.Json(ProfileJson);
        var (gpus, monitors) = MacParsers.Displays(profile);
        Assert.Equal("Apple M3 Pro (14 cores)", Assert.Single(gpus).Name);
        // Der eingebaute Bildschirm ist kein Monitor.
        var monitor = Assert.Single(monitors);
        Assert.Equal(("DELL U2723QE", "ABC123", 2023), (monitor.Model, monitor.SerialNumber, monitor.Year));

        var (slots, modules) = MacParsers.Memory(profile);
        Assert.Null(slots);
        Assert.Equal(("Unified memory", 18L << 30), (Assert.Single(modules).Slot, modules[0].CapacityBytes));
        using var intel = MacParsers.Json(IntelMemoryJson);
        var (intelSlots, intelModules) = MacParsers.Memory(intel);
        Assert.Equal((2, 2, 2667), (intelSlots, intelModules.Count, intelModules[0].SpeedMhz));

        var disk = Assert.Single(MacParsers.Disks(profile));
        Assert.Equal(("APPLE SSD AP0512Z", 500277790720L, "NVMe", "Verified"), (disk.Model, disk.SizeBytes, disk.BusType, disk.Health));
        Assert.Equal((true, 92, 123), MacParsers.Battery(profile));
        Assert.Equal((null, null, null), MacParsers.Battery(MacParsers.Json("{}")));
        var printer = Assert.Single(MacParsers.Printers(profile));
        Assert.Equal(("Buero HP", true, false), (printer.Name, printer.Network, printer.Shared));
        Assert.Equal(500_280_000_000L, MacParsers.Size("500,28 GB", binary: false));
    }

    [Fact]
    public void Security_settings_users_and_network_names_are_read()
    {
        Assert.Equal((true, 100), MacParsers.FileVault("FileVault is On.\n"));
        Assert.Equal((false, 0), MacParsers.FileVault("FileVault is Off.\n"));
        Assert.Equal((false, 45), MacParsers.FileVault("FileVault is On.\nEncryption in progress: Percent completed = 45.2\n"));
        Assert.Equal((null, null), MacParsers.FileVault(null));

        Assert.True(MacParsers.Switch("Firewall is enabled. (State = 1)"));
        Assert.False(MacParsers.Switch("Firewall is disabled. (State = 0)"));
        Assert.True(MacParsers.Switch("Firewall stealth mode is on"));
        Assert.False(MacParsers.Switch("Stealth mode disabled"));
        Assert.True(MacParsers.Switch("assessments enabled"));
        Assert.False(MacParsers.Switch("System Integrity Protection status: disabled."));
        Assert.Null(MacParsers.Switch("System Integrity Protection status: unknown (Custom Configuration)."));
        Assert.Equal((true, false, null), (MacParsers.Defaults("1\n"), MacParsers.Defaults("0"), MacParsers.Defaults(null)));

        Assert.Equal(["max", "anna"], MacParsers.Users("_amavisd 83\nroot 0\nmax 501\nanna 502\nnobody -2\n_mbsetupuser 248\n"));
        Assert.Equal(["Max", "Mustermann"], MacParsers.DsclValues("RealName:\n Max Mustermann\n", "RealName").SelectMany(v => v.Split(' ')).ToList());
        Assert.Equal(["root", "max"], MacParsers.DsclValues("GroupMembership: root max\n", "GroupMembership"));
        Assert.Equal("Wi-Fi", MacParsers.HardwarePorts("Hardware Port: Ethernet\nDevice: en1\nEthernet Address: aa\n\nHardware Port: Wi-Fi\nDevice: en0\n")["en0"]);
        Assert.Equal("Macintosh HD", MacParsers.DiskutilValue("   Volume Name:               Macintosh HD\n   File System Personality:   APFS\n", "Volume Name"));
        Assert.Equal("Disk Image", MacParsers.DiskutilValue("   Volume Name:              SolidCraft\n   Protocol:                  Disk Image\n", "Protocol"));
    }

    [Fact]
    public void The_server_offers_the_mac_agent_per_architecture_and_nothing_for_unknown_runtimes()
    {
        var root = Path.Combine(Path.GetTempPath(), "runners-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "win"));
            File.WriteAllText(Path.Combine(root, "win", JobFiles.RunnerExe), "net48");
            foreach (var runtime in new[] { RunnerRuntimes.MacArm64, RunnerRuntimes.MacX64 })
            {
                Directory.CreateDirectory(Path.Combine(root, "mac", runtime));
                File.WriteAllText(Path.Combine(root, "mac", runtime, AgentProtocol.MacAgentFile), runtime);
            }
            var runner = new RunnerPackage(Path.Combine(root, "win"), null, Path.Combine(root, "mac"));
            Assert.Equal((RunnerRuntimes.MacArm64, RunnerRuntimes.MacX64), (runner.For(RunnerRuntimes.MacArm64)!.Runtime, runner.For(RunnerRuntimes.MacX64)!.Runtime));
            Assert.NotEqual(runner.For(RunnerRuntimes.MacArm64)!.Hash, runner.For(RunnerRuntimes.MacX64)!.Hash);
            // Wer eine unbekannte Laufzeit meldet, bekommt keinen Windows-Runner als Update.
            Assert.Null(runner.For("linux-x64"));
            Assert.Same(runner, runner.For(""));
            Assert.Null(new RunnerPackage(Path.Combine(root, "win")).For(RunnerRuntimes.MacArm64));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentEnrollRequest Mac(string token, string host, string? serial) =>
        new() { EnrollmentToken = token, HostName = host, Version = "1.0", Platform = AgentPlatforms.MacOS, SerialNumber = serial };

    [Fact]
    public async Task A_mac_enrolls_as_mac_is_found_again_by_serial_and_never_takes_over_a_windows_computer()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var agents = new AgentService(db, new SettingsService(db), TimeProvider.System);
        await computers.ImportAsync("pc-01.corp.local");
        var token = await agents.GetEnrollmentTokenAsync();

        Assert.NotNull(await agents.EnrollAsync(Mac(token, "Maxs-MacBook-Pro", "FVFXC0ABCD12")));
        var mac = (await computers.ListAsync()).Single(c => c.Name == "Maxs-MacBook-Pro");
        Assert.Equal((OsPlatform.MacOS, "FVFXC0ABCD12", TransportKind.Agent), (mac.Platform, mac.SerialNumber, mac.Transport));

        // Umbenannt: dieselbe Seriennummer, derselbe Eintrag, mit neuem Namen.
        Assert.NotNull(await agents.EnrollAsync(Mac(token, "Max-MBP", "FVFXC0ABCD12")));
        Assert.Equal(["Max-MBP", "pc-01.corp.local"], (await computers.ListAsync()).Select(c => c.Name));

        // Ein Windows-Rechner gleichen Namens wird nicht zum Mac.
        var refused = await agents.EnrollWithReasonAsync(Mac(token, "PC-01", "C02OTHER"));
        Assert.Null(refused.Key);
        Assert.Contains("not a macOS computer", refused.Refused);
        Assert.Equal(OsPlatform.Windows, (await computers.ListAsync()).Single(c => c.Name.StartsWith("pc-01")).Platform);
        // Und umgekehrt bekommt ein Windows-Agent nicht den Eintrag eines Macs.
        Assert.NotNull((await agents.EnrollWithReasonAsync(new AgentEnrollRequest { EnrollmentToken = token, HostName = "MAX-MBP" })).Refused);
    }

    [Fact]
    public async Task Mac_inventory_is_stored_with_platform_security_and_history()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("MAC-01")).Single().Id;
        var data = SimulatedInventory.Create("MAC-01");
        Assert.Equal(AgentPlatforms.MacOS, data.Platform);
        data.MacOs!.FirewallEnabled = true;
        data.Hardware!.Volumes[0].Protected = true;
        await store.SaveAsync(id, data);

        var computer = (await computers.GetDetailAsync(id))!;
        Assert.Equal((OsPlatform.MacOS, true, true, true), (computer.Platform, computer.MacFirewall, computer.MacGatekeeper, computer.SystemDriveProtected));
        Assert.False(computer.FirewallOff);
        Assert.NotNull(computer.ChassisType);
        Assert.True(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.Platform, Operator = RuleOperator.Equals, Value = "MacOS" }, computer));
        Assert.False(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.Platform, Operator = RuleOperator.Equals, Value = "Windows" }, computer));
        Assert.True(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.DiskFreeGb, Operator = RuleOperator.GreaterOrEqual, Value = "1" }, computer));

        // Firewall und FileVault aus: als Änderung im Verlauf, und die Firewall gilt als aus.
        data.MacOs.FirewallEnabled = false;
        data.Hardware.Volumes[0].Protected = false;
        await store.SaveAsync(id, data);
        computer = (await computers.GetDetailAsync(id))!;
        Assert.True(computer.FirewallOff);
        Assert.Equal(false, computer.SystemDriveProtected);
        var changes = (await store.ChangesAsync(id)).Select(c => c.Change.Item).ToList();
        Assert.Contains("macOS firewall", changes);
        Assert.Contains("/", (await store.ChangesAsync(id)).Where(c => c.Change.Category == "FileVault").Select(c => c.Change.Item));
    }

    [Fact]
    public async Task A_mac_is_online_through_its_agent_and_gets_no_windows_jobs()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var id = (await computers.ImportAsync("MAC-02")).Single().Id;
        await using (var context = db.CreateDbContext())
        {
            var mac = await context.Computers.SingleAsync(c => c.Id == id);
            mac.Platform = OsPlatform.MacOS;
            mac.AgentKeyHash = "x";
            mac.AgentLastSeenUtc = DateTime.UtcNow.AddMinutes(-1);
            mac.Transport = TransportKind.Agent;
            await context.SaveChangesAsync();
        }
        Assert.Equal(1, await computers.ProbeAsync([id]));
        await using (var context = db.CreateDbContext())
            await context.Computers.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.AgentLastSeenUtc, DateTime.UtcNow.AddMinutes(-10)));
        Assert.Equal(0, await computers.ProbeAsync([id]));

        var transport = new SimulatedTransport(TransportKind.Agent);
        var jobs = new RemoteJobService([transport], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db));
        var command = await jobs.RunAsync(id, new JobManifest { Steps = [new JobStep { Name = "x", Type = StepType.PowerShell, Script = "hostname" }] });
        Assert.False(command.Success);
        Assert.Contains("Not available for macOS yet", command.Error);
        var scan = await jobs.RunAsync(id, new JobManifest { Steps = [new JobStep { Name = "scan", Type = StepType.InventoryScan }] });
        Assert.True(scan.Success, scan.Error);
        // Den Agenten entfernen geht auch auf dem Mac.
        Assert.True((await jobs.RunAsync(id, new JobManifest { Steps = [new JobStep { Name = "remove", Type = StepType.UninstallAgent }] })).Success);
    }

    [Fact]
    public async Task The_install_script_carries_server_and_secret_and_fails_when_signing_fails()
    {
        using var db = new TestDb();
        var agents = new AgentService(db, new SettingsService(db), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agents.BuildMacInstallScriptAsync());
        await agents.SetServerUrlAsync("https://inventory.example.local:5100/");
        var script = await agents.BuildMacInstallScriptAsync();

        Assert.StartsWith("#!/bin/bash", script);
        Assert.Contains("SERVER='https://inventory.example.local:5100'", script);
        Assert.Contains($"TOKEN='{await agents.GetEnrollmentTokenAsync()}'", script);
        Assert.Contains($"\"$SERVER/{AgentProtocol.DownloadPath}/$RUNTIME\"", script);
        // launchd setzt keine Shell-Variablen ein: der Pfad steht ausgeschrieben in der plist.
        Assert.Contains($"<string>{AgentService.MacAgentDirectory}/{AgentProtocol.MacAgentFile}</string>", script);
        // Eine fehlgeschlagene Signatur bricht ab (set -e), statt einen Agenten einzurichten, der nicht startet.
        Assert.Contains("set -euo pipefail", script);
        Assert.DoesNotContain("codesign --force --sign - \"$DOWNLOAD\" >/dev/null 2>&1 || true", script);
        Assert.Contains("--uninstall", script);
    }

    [Fact]
    public void The_removal_script_waits_for_the_result_and_unloads_the_daemon_last()
    {
        var script = MacAgentRemoval.Script;
        var wait = script.IndexOf($"\"$1/{AgentProtocol.PendingFolder}/\"*.json", StringComparison.Ordinal);
        var removeFiles = script.IndexOf("rm -rf \"$1\"", StringComparison.Ordinal);
        var bootout = script.IndexOf($"launchctl bootout system/{AgentProtocol.MacAgentLabel}", StringComparison.Ordinal);
        // Erst warten, dann löschen, zuletzt entladen: das Entladen beendet auch dieses Skript.
        Assert.True(wait > 0 && removeFiles > wait && bootout > removeFiles, script);
        Assert.Contains($"rm -f '/Library/LaunchDaemons/{AgentProtocol.MacAgentLabel}.plist'", script);
    }
}
