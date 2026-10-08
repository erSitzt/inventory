using Inventory.Contracts;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Speichert Scan-Ergebnisse; pro Rechner wird nur der jeweils letzte Stand gehalten.</summary>
public sealed class InventoryStore(IDbContextFactory<InventoryDbContext> dbFactory)
{
    // Scans laufen parallel, SQLite erlaubt aber nur einen Schreiber: nacheinander speichern statt auf Sperrkonflikte zu laufen.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task SaveAsync(int computerId, InventoryData data, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await SaveCoreAsync(computerId, data, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SaveCoreAsync(int computerId, InventoryData data, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var computer = await db.Computers.FindAsync([computerId], ct) ?? throw new KeyNotFoundException($"Rechner {computerId} nicht gefunden");

        var now = DateTime.UtcNow;
        computer.LastScanUtc = now;
        computer.LastScanError = null;
        computer.LastSeenUtc = now;
        computer.IsOnline = true;
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

        await db.Software.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Hotfixes.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Disks.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.NetworkAdapters.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Services.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);

        db.Software.AddRange(data.Software.Select(s => new ComputerSoftware
        {
            ComputerId = computerId, Name = s.Name, Version = s.Version, Publisher = s.Publisher, InstallDate = s.InstallDate,
            Is64Bit = s.Is64Bit, PerUser = s.PerUser, UninstallString = s.UninstallString,
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

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SaveErrorAsync(int computerId, string error, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Computers.Where(c => c.Id == computerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastScanError, error), ct);
    }
}
