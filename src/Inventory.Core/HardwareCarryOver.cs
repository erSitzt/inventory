using Inventory.Contracts;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>
/// Ein Bereich, der sich bei einem Scan nicht lesen ließ (z. B. Zugriff verweigert), ist nicht verschwunden: seine Werte
/// werden aus dem gespeicherten Stand übernommen, damit weder Daten verloren gehen noch Scheinänderungen im Verlauf entstehen.
/// </summary>
public static class HardwareCarryOver
{
    /// <param name="previous">Rechner mit geladenen Hardware-Listen.</param>
    public static void Apply(Computer previous, HardwareInfo hardware)
    {
        bool Failed(string area) => hardware.Failed.Contains(area, StringComparer.OrdinalIgnoreCase);

        if (Failed(HardwareAreas.Firmware))
        {
            hardware.ChassisType = previous.ChassisType;
            hardware.FirmwareType = previous.FirmwareType;
            hardware.SecureBoot = previous.SecureBoot;
            hardware.BiosDateUtc = previous.BiosDateUtc;
        }
        if (Failed(HardwareAreas.Tpm))
        {
            hardware.TpmPresent = previous.TpmPresent;
            hardware.TpmVersion = previous.TpmVersion;
            hardware.TpmReady = previous.TpmReady;
        }
        if (Failed(HardwareAreas.Graphics))
            hardware.Gpus = (previous.Gpu ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(name => new GpuItem { Name = name }).ToList();
        if (Failed(HardwareAreas.Memory))
        {
            hardware.MemorySlots = previous.MemorySlots;
            hardware.MemoryModules = previous.MemoryModules.Select(m => new MemoryModuleItem
            {
                Slot = m.Slot, Manufacturer = m.Manufacturer, PartNumber = m.PartNumber, SerialNumber = m.SerialNumber,
                CapacityBytes = m.CapacityBytes, SpeedMhz = m.SpeedMhz, Type = m.Type,
            }).ToList();
        }
        if (Failed(HardwareAreas.Disks))
        {
            hardware.PhysicalDisks = previous.PhysicalDisks.Select(d => new PhysicalDiskItem
            {
                Model = d.Model, SerialNumber = d.SerialNumber, MediaType = d.MediaType, BusType = d.BusType, SizeBytes = d.SizeBytes, Health = d.Health,
            }).ToList();
        }
        if (Failed(HardwareAreas.Volumes))
        {
            hardware.Volumes = previous.Volumes.Select(v => new VolumeEncryptionItem
            {
                Drive = v.Drive, Protected = v.Protected, EncryptedPercent = v.EncryptedPercent, Method = v.Method,
            }).ToList();
        }
        if (Failed(HardwareAreas.Battery))
        {
            hardware.BatteryPresent = previous.BatteryPresent;
            hardware.BatteryHealthPercent = previous.BatteryHealthPercent;
            hardware.BatteryCycleCount = previous.BatteryCycleCount;
        }
        if (Failed(HardwareAreas.Monitors))
        {
            hardware.Monitors = previous.Monitors.Select(m => new MonitorItem
            {
                Manufacturer = m.Manufacturer, Model = m.Model, SerialNumber = m.SerialNumber, Year = m.Year,
            }).ToList();
        }
        if (Failed(HardwareAreas.Printers))
        {
            hardware.Printers = previous.Printers.Select(p => new PrinterItem
            {
                Name = p.Name, Driver = p.Driver, Port = p.Port, Network = p.Network, Shared = p.Shared,
            }).ToList();
        }
    }
}
