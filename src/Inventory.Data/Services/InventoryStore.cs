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
                           .Include(c => c.ServerFeatures).Include(c => c.SqlInstances).Include(c => c.VirtualMachines)
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
            computer.DirectoryInactive = false;
            computer.IsOnline = true;
        }
        else
        {
            computer.IsOnline = false;
        }
        computer.Domain = data.Domain;
        // Ein Domänenmitglied meldet seine DNS-Domäne (eine Arbeitsgruppe hat keinen Punkt); nur als Lückenfüller, das Verzeichnis geht vor.
        if (computer.DnsName is null && data.ComputerName is { } own && data.Domain is { } domain && domain.Contains('.'))
            computer.DnsName = MachineNames.ValidDnsName($"{own}.{domain}", computer.Name);
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
            // null = der Update-Agent hat nicht geantwortet: der vorige Stand bleibt, statt als "nichts ausstehend" zu gelten.
            if (PendingUpdateRows(computerId, security) is { } pendingUpdates)
            {
                computer.UpdatePendingCount = pendingUpdates.Count;
                computer.UpdatePendingSecurityCount = pendingUpdates.Count(u => u.IsSecurity);
                computer.UpdatePending = string.Join('\n', pendingUpdates.Select(u => u.Title));
                await db.PendingUpdates.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
                db.PendingUpdates.AddRange(pendingUpdates);
            }
            computer.UpdateServer = security.UpdateServer;
            if (security.Posture is { } posture)
            {
                // Ein Wert, der sich diesmal nicht lesen ließ, ist nicht verschwunden: den vorigen behalten.
                computer.RdpEnabled = posture.RdpEnabled ?? computer.RdpEnabled;
                computer.RdpNla = posture.RdpNla ?? computer.RdpNla;
                computer.UacEnabled = posture.UacEnabled ?? computer.UacEnabled;
                computer.UacLevel = posture.UacLevel ?? computer.UacLevel;
                computer.Smb1Enabled = posture.Smb1Enabled ?? computer.Smb1Enabled;
                computer.CredentialGuard = posture.CredentialGuard ?? computer.CredentialGuard;
                computer.MemoryIntegrity = posture.MemoryIntegrity ?? computer.MemoryIntegrity;
                computer.LsaProtection = posture.LsaProtection ?? computer.LsaProtection;
                computer.BuiltinAdminEnabled = posture.BuiltinAdminEnabled ?? computer.BuiltinAdminEnabled;
                computer.BuiltinAdminPasswordAgeDays = posture.BuiltinAdminPasswordAgeDays ?? computer.BuiltinAdminPasswordAgeDays;
                computer.WindowsActivated = posture.WindowsActivated ?? computer.WindowsActivated;
            }
        }

        await db.Software.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Hotfixes.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Disks.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.NetworkAdapters.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.Services.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.LocalUsers.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        await db.LocalAdmins.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);

        // null = ein älterer Runner hat die Apps nicht gelesen: dann bleibt der bisherige Stand.
        if (data.Apps is { } apps)
        {
            await db.Apps.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
            db.Apps.AddRange(apps.Select(a => new ComputerApp
            {
                ComputerId = computerId, Name = a.Name, DisplayName = a.DisplayName, Version = a.Version, Publisher = a.Publisher,
                Architecture = a.Architecture, Provisioned = a.Provisioned,
            }));
        }
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
            IpAddresses = string.Join(", ", n.IpAddresses), SubnetMasks = n.SubnetMasks.Count > 0 ? string.Join(", ", n.SubnetMasks) : null, Gateways = string.Join(", ", n.Gateways), DnsServers = string.Join(", ", n.DnsServers),
        }));
        db.Services.AddRange(data.Services.Select(s => new ComputerWindowsService
        {
            ComputerId = computerId, Name = s.Name, DisplayName = s.DisplayName, State = s.State, StartMode = s.StartMode, Account = s.Account,
        }));

        db.LocalUsers.AddRange(data.LocalUsers.Select(u => new ComputerLocalUser
        {
            ComputerId = computerId, Name = u.Name, FullName = u.FullName, Description = u.Description, Disabled = u.Disabled, Sid = u.Sid,
            PasswordAgeDays = u.PasswordAgeDays, LastLogonUtc = u.LastLogonUtc,
        }));
        db.LocalAdmins.AddRange(data.LocalAdmins.Select(a => new ComputerLocalAdmin
        {
            ComputerId = computerId, Name = a.Name, IsGroup = a.IsGroup, Sid = a.Sid,
        }));

        await SaveUsersAsync(db, computer, data, now, ct);

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

        if (data.Licensing is { } licensing)
            await SaveLicensingAsync(db, computer, licensing, now, ct);

        await db.SaveChangesAsync(ct);
        await VirtualHosts.ResolveAsync(db, computer.Id, ct);
        await transaction.CommitAsync(ct);
        return computer.NameMismatch;
    }

    /// <summary>
    /// Angaben für die Lizenzberechnung. Ein Wert, der sich diesmal nicht lesen ließ, bleibt wie bisher; eine Liste, die
    /// fehlt (null), ebenso. Eine leere Liste heißt dagegen: nichts davon vorhanden.
    /// </summary>
    private static async Task SaveLicensingAsync(InventoryDbContext db, Computer computer, LicensingInfo licensing, DateTime now, CancellationToken ct)
    {
        var computerId = computer.Id;
        computer.LicensingScanUtc = now;
        computer.CpuSockets = licensing.CpuSockets ?? computer.CpuSockets;
        computer.CpuCoresPerSocket = licensing.CpuCoresPerSocket ?? computer.CpuCoresPerSocket;
        computer.BiosUuid = BiosUuids.Normalize(licensing.BiosUuid) ?? computer.BiosUuid;
        computer.HypervisorPresent = licensing.HypervisorPresent ?? computer.HypervisorPresent;
        computer.HyperVGuest = licensing.HyperVGuest ?? computer.HyperVGuest;
        // Ohne Gastdienste gibt es keinen gemeldeten Host mehr (z. B. nach einer Migration zu einem anderen Hypervisor).
        computer.ReportedHostName = licensing.HyperVGuest is null ? computer.ReportedHostName : licensing.ReportedHostName;
        computer.OsSku = licensing.OsSku ?? computer.OsSku;
        computer.OsProductType = licensing.OsProductType ?? computer.OsProductType;
        computer.OsEdition = licensing.OsEdition ?? computer.OsEdition;
        computer.LicenseChannel = licensing.LicenseChannel ?? computer.LicenseChannel;
        computer.ClusterName = licensing.ClusterName;

        if (licensing.ServerFeatures is { } features)
        {
            await db.ServerFeatures.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
            db.ServerFeatures.AddRange(features.Select(f => new ComputerServerFeature { ComputerId = computerId, FeatureId = f.Id, Name = f.Name }));
        }
        else if (licensing.OsProductType == 1)
        {
            // Ein Arbeitsplatzsystem hat keine Serverrollen (etwa nach einer Neuinstallation auf derselben Hardware).
            await db.ServerFeatures.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        }
        if (licensing.SqlInstances is { } instances)
        {
            await db.SqlInstances.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
            db.SqlInstances.AddRange(instances.Select(i => new ComputerSqlInstance
            {
                ComputerId = computerId, Name = i.Name, InstanceId = i.InstanceId, Edition = i.Edition, Version = i.Version, PatchLevel = i.PatchLevel, Clustered = i.Clustered,
            }));
        }
        // null heißt: kein Hyper-V-Host (mehr), dann verschwinden seine VMs. Nur wenn die Hyper-V-Rolle installiert ist, war
        // es eher ein Lesefehler, und der bisherige Stand bleibt.
        var hyperVRole = licensing.ServerFeatures?.Any(f => f.Id == ServerFeatures.HyperV) == true;
        if (licensing.VirtualMachines is not null || !hyperVRole)
            await db.HostedVirtualMachines.Where(x => x.ComputerId == computerId).ExecuteDeleteAsync(ct);
        if (licensing.VirtualMachines is { } machines)
            db.HostedVirtualMachines.AddRange(machines.Select(v => new HostedVirtualMachine
            {
                ComputerId = computerId, Name = v.Name, BiosUuid = BiosUuids.Normalize(v.BiosUuid), VirtualCpus = v.VirtualCpus, Running = v.Running,
            }));
    }

    /// <summary>Über diesen Zeitraum wird beobachtet, wer ein Gerät nutzt.</summary>
    public const int PrimaryUserDays = 30;

    /// <summary>
    /// Speichert die Benutzerprofile, merkt sich, wer gerade interaktiv angemeldet ist, und leitet daraus den Hauptbenutzer ab.
    /// Konten, mit denen der Server selbst auf die Rechner zugreift, zählen dabei nie.
    /// </summary>
    private static async Task SaveUsersAsync(InventoryDbContext db, Computer computer, InventoryData data, DateTime now, CancellationToken ct)
    {
        var serviceAccounts = (await db.Credentials.AsNoTracking().Select(c => c.Username).ToListAsync(ct)).Select(AccountName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(now);
        var since = today.AddDays(-PrimaryUserDays);
        await db.UserSightings.Where(s => s.ComputerId == computer.Id && s.Day < since).ExecuteDeleteAsync(ct);
        var sightings = await db.UserSightings.Where(s => s.ComputerId == computer.Id).ToListAsync(ct);

        // Ältere Runner kennen nur den Benutzer an der Konsole.
        var interactive = data.InteractiveUsers ?? (string.IsNullOrWhiteSpace(data.LoggedOnUser) ? [] : [data.LoggedOnUser]);
        foreach (var user in interactive.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (sightings.Any(s => s.Day == today && string.Equals(s.UserName, user, StringComparison.OrdinalIgnoreCase)))
                continue;
            var sighting = new ComputerUserSighting { ComputerId = computer.Id, UserName = user, Day = today };
            db.UserSightings.Add(sighting);
            sightings.Add(sighting);
        }

        // Ältere Runner nennen nur die SIDs: ohne Namen gibt es nichts anzuzeigen, der vorige Stand bleibt.
        var named = data.UserProfiles?.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        if (named is { Count: > 0 })
        {
            await db.UserProfiles.Where(x => x.ComputerId == computer.Id).ExecuteDeleteAsync(ct);
            db.UserProfiles.AddRange(named.Select(p => new ComputerUserProfile { ComputerId = computer.Id, UserName = p.Name!, Sid = p.Sid, LastUseUtc = p.LastUseUtc, Loaded = p.Loaded }));
        }

        // Nur beobachtete interaktive Sitzungen zählen: ein bloß vorhandenes oder geladenes Profil (Dienst, geplante Aufgabe,
        // Zugriff des Servers) sagt nichts darüber, wer am Gerät arbeitet. Ohne Beobachtung gibt es keinen Hauptbenutzer.
        computer.PrimaryUser = sightings.Where(s => !serviceAccounts.Contains(AccountName(s.UserName)))
            .GroupBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(s => s.Day)).FirstOrDefault()?.Key;
    }

    /// <summary>Kontoname ohne Domäne oder Rechner: aus DOMÄNE\\name, name@domäne und .\\name wird name.</summary>
    internal static string AccountName(string user)
    {
        var name = user.Trim();
        var slash = name.LastIndexOf('\\');
        if (slash >= 0)
            name = name[(slash + 1)..];
        var at = name.IndexOf('@');
        return at > 0 ? name[..at] : name;
    }

    /// <summary>
    /// Einmalig nach dem Update des Servers: Rechner, deren letzter Scan nur die Titel der ausstehenden Updates gespeichert
    /// hat, bekommen daraus ihre Einträge, damit die Update-Übersicht nicht erst nach dem nächsten Scan stimmt.
    /// </summary>
    /// <returns>Anzahl der ergänzten Rechner.</returns>
    public async Task<int> BackfillPendingUpdatesAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var computers = await db.Computers.Where(c => c.UpdatePendingCount != null && c.UpdatePendingSecurityCount == null).ToListAsync(ct);
            foreach (var computer in computers)
            {
                var rows = PendingUpdateRows(computer.Id, new SecurityStatus
                {
                    UpdatePending = (computer.UpdatePending ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                })!;
                db.PendingUpdates.AddRange(rows);
                computer.UpdatePendingSecurityCount = rows.Count(u => u.IsSecurity);
            }
            await db.SaveChangesAsync(ct);
            return computers.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Ausstehende Updates eines Scans; ältere Runner liefern nur Titel, aus denen die KB-Nummer gelesen wird.</summary>
    private static List<ComputerPendingUpdate>? PendingUpdateRows(int computerId, SecurityStatus security)
    {
        if (security.UpdatePendingDetails is { } details)
        {
            return details.Select(u => new ComputerPendingUpdate
            {
                ComputerId = computerId, Title = u.Title, KbIds = u.KbIds.Count == 0 ? null : string.Join(", ", u.KbIds),
                Classification = u.Classification, Severity = u.Severity, IsDownloaded = u.IsDownloaded, SizeBytes = u.SizeBytes,
            }).ToList();
        }
        return security.UpdatePending?.Select(title => new ComputerPendingUpdate
        {
            ComputerId = computerId, Title = title,
            KbIds = PendingUpdates.KbIdsFromTitle(title) is { Count: > 0 } ids ? string.Join(", ", ids) : null,
        }).ToList();
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
