using Inventory.Contracts;
using Inventory.Core;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class HardwareTests
{
    private static InventoryData Scan(Action<HardwareInfo>? change = null)
    {
        var hardware = new HardwareInfo
        {
            ChassisType = 10, FirmwareType = "UEFI", SecureBoot = true, TpmPresent = true, TpmVersion = "2.0", TpmReady = true,
            Gpus = [new GpuItem { Name = "Intel UHD" }], MemorySlots = 2, BatteryPresent = true, BatteryHealthPercent = 87,
            MemoryModules = [new MemoryModuleItem { Slot = "DIMM 1", Manufacturer = "Samsung", PartNumber = "M471", CapacityBytes = 8L << 30 }],
            PhysicalDisks = [new PhysicalDiskItem { Model = "SSD 512", SerialNumber = "S1", MediaType = "SSD", Health = "Healthy", SizeBytes = 512_000_000_000 }],
            Volumes = [new VolumeEncryptionItem { Drive = "C:", Protected = true, EncryptedPercent = 100, Method = "XTS-AES 256" }, new VolumeEncryptionItem { Drive = "D:", Protected = false }],
            Monitors = [new MonitorItem { Manufacturer = "DEL", Model = "DELL P2422H", SerialNumber = "M1", Year = 2022 }],
            Printers = [new PrinterItem { Name = "Laser", Port = "10.0.0.5" }],
        };
        change?.Invoke(hardware);
        return new InventoryData { ComputerName = "PC-01", Hardware = hardware };
    }

    [Fact]
    public async Task Hardware_is_stored_and_a_scan_without_it_keeps_the_previous_state()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;

        await store.SaveAsync(id, Scan());
        // Ein älterer Runner liefert keine Hardware-Details.
        await store.SaveAsync(id, new InventoryData { ComputerName = "PC-01" });

        var detail = (await computers.GetDetailAsync(id))!;
        Assert.Equal(ChassisKind.Laptop, Chassis.Kind(detail.ChassisType, detail.Model));
        Assert.Equal("UEFI", detail.FirmwareType);
        Assert.True(detail.SecureBoot);
        Assert.True(detail.TpmReady);
        Assert.Equal("Intel UHD", detail.Gpu);
        Assert.Equal(87, detail.BatteryHealthPercent);
        Assert.True(detail.SystemDriveProtected);
        Assert.Equal("DIMM 1", Assert.Single(detail.MemoryModules).Slot);
        Assert.Equal("SSD", Assert.Single(detail.PhysicalDisks).MediaType);
        Assert.Equal(2, detail.Volumes.Count);
        // Die PnP-Kennung des Herstellers wird ausgeschrieben.
        Assert.Equal("Dell", Assert.Single(detail.Monitors).Manufacturer);
        Assert.Equal("Laser", Assert.Single(detail.Printers).Name);
        Assert.Empty(await store.ChangesAsync(id));
    }

    [Fact]
    public async Task First_scan_with_hardware_is_the_baseline_and_later_scans_record_what_changed()
    {
        using var db = new TestDb();
        var store = new InventoryStore(db);
        var id = (await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01"))[0].Id;

        // Erst ein Scan ohne, dann einer mit Hardware-Details: nichts davon ist eine Änderung.
        await store.SaveAsync(id, new InventoryData { ComputerName = "PC-01" });
        await store.SaveAsync(id, Scan());
        Assert.Empty(await store.ChangesAsync(id));

        await store.SaveAsync(id, Scan(h =>
        {
            h.SecureBoot = false;
            h.TpmReady = false;
            h.MemoryModules.Add(new MemoryModuleItem { Slot = "DIMM 2", Manufacturer = "Samsung", PartNumber = "M471", CapacityBytes = 8L << 30 });
            h.PhysicalDisks[0].Health = "Warning";
            h.Volumes[0].Protected = false;
            h.Monitors.Clear();
            h.Printers.Clear();
        }));

        var changes = (await store.ChangesAsync(id)).Select(c => $"{c.Change.Category}|{c.Change.Kind}|{c.Change.Item}|{c.Change.OldValue}|{c.Change.NewValue}").Order().ToList();
        Assert.Equal(
        [
            "BitLocker|Changed|C:|on|off",
            "Memory module|Added|DIMM 2||8 GB Samsung M471",
            "Physical disk|Changed|SSD 512 (S1)|Healthy|Warning",
            // Monitore wechseln bei Notebooks ständig und erscheinen nicht im Verlauf.
            "Printer|Removed|Laser|10.0.0.5|",
            "Security|Changed|Secure Boot|on|off",
            "Security|Changed|TPM|ready|not ready",
        ], changes);
    }

    [Theory]
    [InlineData(3, "OptiPlex 7090", ChassisKind.Desktop)]
    [InlineData(35, "ThinkCentre M70q", ChassisKind.Desktop)]
    [InlineData(10, "Latitude 5520", ChassisKind.Laptop)]
    [InlineData(31, "Surface Laptop", ChassisKind.Laptop)]
    [InlineData(23, "ProLiant DL380", ChassisKind.Server)]
    [InlineData(3, "Virtual Machine", ChassisKind.VirtualMachine)]
    [InlineData(1, "VMware7,1", ChassisKind.VirtualMachine)]
    [InlineData(null, null, ChassisKind.Unknown)]
    public void Chassis_types_map_to_device_classes(int? type, string? model, ChassisKind expected) =>
        Assert.Equal(expected, Chassis.Kind(type, model));

    [Fact]
    public void Simulated_computers_report_hardware()
    {
        var hardware = SimulatedInventory.Create("pc-01").Hardware!;
        Assert.NotEmpty(hardware.MemoryModules);
        Assert.NotEmpty(hardware.PhysicalDisks);
        Assert.NotNull(hardware.ChassisType);
    }
}
