using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Speichert Scan-Ergebnisse; pro Rechner wird nur der jeweils letzte Stand gehalten.</summary>
public sealed class InventoryStore(IDbContextFactory<InventoryDbContext> dbFactory, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    // Scans laufen parallel, SQLite erlaubt aber nur einen Schreiber: nacheinander speichern statt auf Sperrkonflikte zu laufen.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <returns>true, wenn sich der Rechner mit einem anderen Namen gemeldet hat als eingetragen.</returns>
    public async Task<bool> SaveAsync(int computerId, InventoryData data, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            return await SaveCoreAsync(computerId, data, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> SaveCoreAsync(int computerId, InventoryData data, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Mit dem bisherigen Stand laden: aus dem Vergleich entsteht der Verlauf.
        var computer = await db.Computers.AsSplitQuery()
                           .Include(c => c.Software).Include(c => c.Hotfixes).Include(c => c.Disks).Include(c => c.NetworkAdapters)
                           .Include(c => c.Services).Include(c => c.LocalUsers).Include(c => c.LocalAdmins)
                           .Include(c => c.MemoryModules).Include(c => c.PhysicalDisks).Include(c => c.Volumes).Include(c => c.Monitors).Include(c => c.Printers)
                           .FirstOrDefaultAsync(c => c.Id == computerId, ct)
                       ?? throw new KeyNotFoundException(L["Computer {0} not found", computerId]);

        var now = DateTime.UtcNow;
        // Vor dem Vergleich: Programme abgemeldeter Benutzer sind nicht verschwunden, nur gerade nicht lesbar.
        PerUserSoftware.CarryOver(computer.Software, data);
        if (data.Hardware is not null)
            HardwareCarryOver.Apply(computer, data.Hardware);
        db.InventoryChanges.AddRange(InventoryDiff.Compare(computer, data, now));
        db.ChangeTracker.Entries().Where(e => e.Entity is not Computer and not InventoryChange).ToList().ForEach(e => e.State = EntityState.Detached);
        computer.LastScanUtc = now;
        computer.LastScanError = null;
        computer.ReportedName = data.ComputerName;
        // Hat ein anderer Rechner geantwortet, ist der eingetragene damit nicht als erreichbar belegt.
        if (!(computer.NameMismatch && await SettingsService.VerifyTargetNameAsync(db, ct)))
        {
            computer.LastSeenUtc = now;
            computer.IsOnline = true;
        }
        else
        {
            computer.IsOnline = false;
        }
        computer.Domain = data.Domain;
        computer.Manufacturer = data.Manufacturer;
        computer.Model = data.Model;
        computer.SerialNumber = data.SerialNumber;
        computer.BiosVersion = data.BiosVersion;
        computer.OsName = data.OsName;
        computer.OsVersion = data.OsVersion;
        computer.OsRelease = data.OsRelease;
        computer.OsArchitecture = data.OsArchitecture;
        computer.OsInstalledUtc = data.OsInstalledUtc;
        computer.LastBootUtc = data.LastBootUtc;
        computer.CpuName = data.CpuName;
        computer.CpuCores = data.CpuCores;
        computer.CpuLogicalProcessors = data.CpuLogicalProcessors;
        computer.MemoryBytes = data.MemoryBytes;
        computer.LoggedOnUser = data.LoggedOnUser;
        if (data.RemoteAccess is { } remote)
        {
            computer.SessionId = remote.SessionId;
            computer.TeamViewerId = remote.TeamViewerId;
            computer.AnyDeskId = remote.AnyDeskId;
        }
        if (data.Security is { } security)
        {
            computer.FirewallDomain = security.FirewallDomain;
            computer.FirewallPrivate = security.FirewallPrivate;
            computer.FirewallPublic = security.FirewallPublic;
            computer.DefenderEnabled = security.DefenderEnabled;
            computer.DefenderRealTime = security.DefenderRealTime;
            computer.DefenderTamperProtected = security.DefenderTamperProtected;
            computer.DefenderSignatureVersion = security.DefenderSignatureVersion;
            computer.DefenderSignatureUpdatedUtc = security.DefenderSignatureUpdatedUtc;
            computer.DefenderMode = security.DefenderMode;
        computer.DefenderLastQuickScanUtc = security.DefenderLastQuickScanUtc;
        // Ohne Zustandsangaben (ältere Runner, Server-Betriebssysteme) gelten alle registrierten Produkte als eingeschaltet.
        var details = security.AntivirusProductDetails;
        computer.AntivirusProducts = string.Join(", ", details.Count > 0 ? details.Where(p => p.Enabled).Select(p => p.Name) : security.AntivirusProducts);
        computer.AntivirusProductsInactive = details.Count > 0 ? string.Join(", ", details.Where(p => !p.Enabled).Select(p => p.Name)) : null;
        var signatureStale = security.DefenderRealTime == true && security.DefenderSignatureUpdatedUtc is { } signature && now - signature > TimeSpan.FromDays(7);
        computer.AntivirusDefinitionsOutdated = details.Count > 0 || security.DefenderSignatureUpdatedUtc is not null
            ? details.Any(p => p.Enabled && !p.UpToDate) || signatureStale
            : null;
            computer.UpdateLastSearchUtc = security.UpdateLastSearchUtc;
            computer.UpdateLastInstallUtc = security.UpdateLastInstallUtc;
            computer.UpdateRebootRequired = security.UpdateRebootRequired;
            computer.UpdatePendingCount = security.UpdatePending?.Count;
            computer.UpdatePending = security.UpdatePending is null ? null : string.Join('\n', security.UpdatePending);
            computer.UpdateServer = security.UpdateServer;
        }

        await db.Software.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Hotfixes.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Disks.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.NetworkAdapters.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Services.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.LocalUsers.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.LocalAdmins.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);

        db.Software.AddRange(data.Software.Select(s => new ComputerSoftware
        {
            ComputerId = computerId, Name = s.Name, Version = s.Version, Publisher = s.Publisher, InstallDate = s.InstallDate,
            Is64Bit = s.Is64Bit, PerUser = s.PerUser, UninstallString = s.UninstallString, UserSid = s.UserSid, CarriedOver = s.CarriedOver,
        }));
        db.Hotfixes.AddRange(data.Hotfixes.Select(h => new ComputerHotfix
        {
            ComputerId = computerId, HotfixId = h.Id, Description = h.Description, InstalledOn = h.InstalledOn,
        }));
        db.Disks.AddRange(data.Disks.Select(d => new ComputerDisk
        {
            ComputerId = computerId, Drive = d.Drive, Label = d.Label, FileSystem = d.FileSystem, SizeBytes = d.SizeBytes, FreeBytes = d.FreeBytes,
        }));
        db.NetworkAdapters.AddRange(data.NetworkAdapters.Select(n => new ComputerNetworkAdapter
        {
            ComputerId = computerId, Name = n.Name, MacAddress = n.MacAddress, DhcpEnabled = n.DhcpEnabled,
            IpAddresses = string.Join(", ", n.IpAddresses), Gateways = string.Join(", ", n.Gateways), DnsServers = string.Join(", ", n.DnsServers),
        }));
        db.Services.AddRange(data.Services.Select(s => new ComputerWindowsService
        {
            ComputerId = computerId, Name = s.Name, DisplayName = s.DisplayName, State = s.State, StartMode = s.StartMode, Account = s.Account,
        }));

        db.LocalUsers.AddRange(data.LocalUsers.Select(u => new ComputerLocalUser
        {
            ComputerId = computerId, Name = u.Name, FullName = u.FullName, Description = u.Description, Disabled = u.Disabled, Sid = u.Sid,
        }));
        db.LocalAdmins.AddRange(data.LocalAdmins.Select(a => new ComputerLocalAdmin
        {
            ComputerId = computerId, Name = a.Name, IsGroup = a.IsGroup, Sid = a.Sid,
        }));

        // Ältere Runner liefern keine Hardware-Details: den vorhandenen Stand dann stehen lassen statt ihn zu leeren.
        if (data.Hardware is { } hardware)
        {
            computer.ChassisType = hardware.ChassisType;
            computer.FirmwareType = hardware.FirmwareType;
            computer.SecureBoot = hardware.SecureBoot;
            computer.BiosDateUtc = hardware.BiosDateUtc;
            computer.TpmPresent = hardware.TpmPresent;
            computer.TpmVersion = hardware.TpmVersion;
            computer.TpmReady = hardware.TpmReady;
            computer.Gpu = hardware.Gpus.Count == 0 ? null : string.Join(", ", hardware.Gpus.Select(g => g.Name));
            computer.MemorySlots = hardware.MemorySlots;
            computer.BatteryPresent = hardware.BatteryPresent;
            computer.BatteryHealthPercent = hardware.BatteryHealthPercent;
            computer.BatteryCycleCount = hardware.BatteryCycleCount;
            computer.SystemDriveProtected = SystemDrive(hardware.Volumes)?.Protected;
            computer.HardwareScanUtc = now;

            await db.MemoryModules.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
            await db.PhysicalDisks.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
            await db.Volumes.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
            await db.Monitors.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
            await db.Printers.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);

            db.MemoryModules.AddRange(hardware.MemoryModules.Select(m => new ComputerMemoryModule
            {
                ComputerId = computerId, Slot = m.Slot, Manufacturer = m.Manufacturer, PartNumber = m.PartNumber, SerialNumber = m.SerialNumber,
                CapacityBytes = m.CapacityBytes, SpeedMhz = m.SpeedMhz, Type = m.Type,
            }));
            db.PhysicalDisks.AddRange(hardware.PhysicalDisks.Select(d => new ComputerPhysicalDisk
            {
                ComputerId = computerId, Model = d.Model, SerialNumber = d.SerialNumber, MediaType = d.MediaType, BusType = d.BusType,
                SizeBytes = d.SizeBytes, Health = d.Health,
            }));
            db.Volumes.AddRange(hardware.Volumes.Select(v => new ComputerVolumeEncryption
            {
                ComputerId = computerId, Drive = v.Drive, Protected = v.Protected, EncryptedPercent = v.EncryptedPercent, Method = v.Method,
            }));
            db.Monitors.AddRange(hardware.Monitors.Select(m => new ComputerMonitor
            {
                ComputerId = computerId, Manufacturer = MonitorVendors.Name(m.Manufacturer), Model = m.Model, SerialNumber = m.SerialNumber, Year = m.Year,
            }));
            db.Printers.AddRange(hardware.Printers.Select(p => new ComputerPrinter
            {
                ComputerId = computerId, Name = p.Name, Driver = p.Driver, Port = p.Port, Network = p.Network, Shared = p.Shared,
            }));
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return computer.NameMismatch;
    }

    /// <summary>Das Laufwerk, auf dem Windows liegt; Scans nennen es nicht eigens, in der Praxis ist es C:.</summary>
    private static VolumeEncryptionItem? SystemDrive(List<VolumeEncryptionItem> volumes) =>
        volumes.FirstOrDefault(v => v.Drive.StartsWith("C", StringComparison.OrdinalIgnoreCase));

    /// <summary>Änderungen eines Rechners (computerId gesetzt) oder aller Rechner, neueste zuerst.</summary>
    public async Task<List<(InventoryChange Change, string Computer)>> ChangesAsync(int? computerId = null, int take = 1000, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.InventoryChanges.AsNoTracking()
            .Where(c => computerId == null || c.ComputerId == computerId)
            .OrderByDescending(c => c.Id).Take(take)
            .Join(db.Computers, c => c.ComputerId, k => k.Id, (c, k) => new { Change = c, k.Name })
            .ToListAsync(ct);
        var names = rows.GroupBy(r => r.Change.ComputerId).ToDictionary(g => g.Key, g => g.First().Name);
        // Ein Scan schreibt alle seine Änderungen mit derselben Zeit: zusammenhalten und nach Bereich und Eintrag ordnen,
        // statt in der zufälligen Reihenfolge der Erfassung. Ältere Einträge "entfernt" + "neu" desselben Programms
        // werden wie neue als Update dargestellt.
        return InventoryDiff.MergeUpdates(rows.Select(r => r.Change).ToList())
            .OrderByDescending(c => c.TimeUtc).ThenBy(c => names[c.ComputerId], StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Item, StringComparer.OrdinalIgnoreCase)
            .Select(c => (c, names[c.ComputerId])).ToList();
    }

    /// <summary>Kennung der jüngsten Änderung; ändert sie sich, gibt es Neues anzuzeigen.</summary>
    public async Task<long> LatestChangeIdAsync(int? computerId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.InventoryChanges.Where(c => computerId == null || c.ComputerId == computerId).MaxAsync(c => (long?)c.Id, ct) ?? 0;
    }

    /// <summary>Entfernt Verlaufseinträge, die älter als die Aufbewahrungsfrist sind.</summary>
    public async Task<int> CleanupChangesAsync(TimeSpan keep, CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow - keep;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.InventoryChanges.Where(c => c.TimeUtc < limit).ExecuteDeleteAsync(ct);
    }

    public async Task SaveErrorAsync(int computerId, string error, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Computers.Where(c => c.Id == computerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastScanError, error), ct);
    }
}
