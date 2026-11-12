using System.ComponentModel;
using Inventory.Core.Model;
using Inventory.Data;
using Microsoft.EntityFrameworkCore;
using Inventory.Data.Services;
using Inventory.Server.Api;
using Inventory.Server.Services;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Inventory.Server.Mcp;

/// <summary>Lesende Werkzeuge zu Rechnern, Software, Updates, Paketen und Deployments; für jede Rolle.</summary>
[McpServerToolType, Authorize(Policy = AccessControl.ViewerPolicy)]
public sealed class InventoryTools(McpCaller caller, IDbContextFactory<InventoryDbContext> dbFactory, CustomValueService customValues, ComputerService computerService, CollectionService collections, ReportService reports, PackageService packages,
    DeploymentService deployments)
{
    public sealed record ComputerSummary(int Id, string Name, bool IsOnline, string? OsName, string? OsVersion, string? IpAddress, string? LoggedOnUser,
        string? Model, DateTime? LastSeenUtc, DateTime? LastScanUtc, string? LastScanError, int? PendingUpdates, int? PendingSecurityUpdates, bool? RebootRequired);

    private static ComputerSummary Summary(Computer c) => new(c.Id, c.Name, c.IsOnline, c.OsName, c.OsVersion, c.IpAddress, c.LoggedOnUser, c.Model,
        c.LastSeenUtc, c.LastScanUtc, c.LastScanError, c.UpdatePendingCount, c.UpdatePendingSecurityCount, c.UpdateRebootRequired);

    [McpServerTool(Name = "search_computers", Title = "Search computers", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists inventoried computers with status, operating system, IP, logged-on user, model, last scan and pending updates. "
        + "query matches name, IP, operating system, model, user, owner, location, tags or the values of plugin inventory fields (substring, case-insensitive).")]
    public async Task<object> SearchComputers(
        [Description("Text to search for; empty = all computers.")] string? query = null,
        [Description("true = only reachable computers, false = only unreachable ones.")] bool? online = null,
        [Description("Only members of this collection (name or id).")] string? collection = null,
        CancellationToken ct = default)
    {
        IEnumerable<Computer> list = await computerService.ListAsync(ct);
        if (!string.IsNullOrWhiteSpace(collection))
        {
            var match = McpCaller.Pick(await collections.ListAsync(ct), collection, s => s.Collection.Id, s => s.Collection.Name, "collection");
            var members = (await collections.GetMemberIdsAsync(match.Collection.Id, ct)).ToHashSet();
            list = list.Where(c => members.Contains(c.Id));
        }
        if (online is { } wanted)
            list = list.Where(c => c.IsOnline == wanted);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var custom = customValues.Fields.Count > 0 ? await customValues.AllAsync(ct) : [];
            list = list.Where(c => new[] { c.Name, c.DnsName, c.IpAddress, c.OsName, c.OsVersion, c.Model, c.Manufacturer, c.LoggedOnUser, c.PrimaryUser, c.Owner, c.Location, c.Tags, c.Description }
                .Concat(custom.TryGetValue(c.Id, out var values) ? values.Values : Enumerable.Empty<string>())
                .Any(t => t?.Contains(q, StringComparison.OrdinalIgnoreCase) == true));
        }
        return McpCaller.Page(list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(Summary));
    }

    [McpServerTool(Name = "get_computer", Title = "Computer details", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Master data, status, hardware, security (Defender, firewall, BitLocker, TPM, Secure Boot; on Macs FileVault, firewall, Gatekeeper, SIP), update state, "
        + "optional Windows features that are turned on and WSL (Windows Subsystem for Linux: version and the users' distributions with their last use) of one computer.")]
    public async Task<object> GetComputer([Description("Computer name, DNS name or id.")] string computer, CancellationToken ct = default)
    {
        var c = await computerService.GetDetailAsync((await caller.ComputerAsync(computer, ct)).Id, ct) ?? throw new McpException("The computer no longer exists.");
        return new
        {
            computer = ComputerEndpoints.ToDto(c),
            hardware = new { c.CpuName, c.CpuCores, c.CpuLogicalProcessors, c.MemoryBytes, c.Gpu, c.FirmwareType, c.SecureBoot, c.TpmPresent, c.TpmVersion, c.TpmReady, c.BiosVersion },
            security = new
            {
                c.DefenderEnabled, c.DefenderRealTime, c.DefenderSignatureUpdatedUtc, c.AntivirusProducts, c.FirewallDomain, c.FirewallPrivate, c.FirewallPublic,
                c.UacEnabled, c.RdpEnabled, c.Smb1Enabled, c.CredentialGuard, c.LsaProtection, c.SystemDriveProtected,
                volumes = c.Volumes.Select(v => new { v.Drive, v.Protected, v.EncryptedPercent, v.Method }),
                macOS = c.IsMac ? new { firewall = c.MacFirewall, firewallStealth = c.MacFirewallStealth, gatekeeper = c.MacGatekeeper, sip = c.MacSip, automaticUpdateCheck = c.MacAutoUpdateCheck } : null,
            },
            updates = new { c.UpdatePendingCount, c.UpdatePendingSecurityCount, c.UpdateRebootRequired, c.UpdateLastSearchUtc, c.UpdateLastInstallUtc },
            disks = c.Disks.Select(d => new { d.Drive, d.SizeBytes, d.FreeBytes }),
            network = c.NetworkAdapters.Select(a => new { a.Name, a.MacAddress, a.IpAddresses, a.Gateways, a.DhcpEnabled }),
            agent = new { c.AgentVersion, c.AgentLastSeenUtc },
            // null = noch nicht erfasst (älterer Runner, Mac).
            windowsFeatures = c.FeaturesScanUtc is null ? null : c.OptionalFeatures.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase),
            wsl = c.WslScanUtc is null ? null : new
            {
                installed = c.WslInstalled, version = c.WslVersion,
                distributions = c.WslDistros.Select(d => new { d.Name, user = d.UserName ?? d.UserSid, d.WslVersion, d.LastUsedUtc, d.SizeBytes, fromEarlierScan = d.CarriedOver }),
            },
            customFields = (await customValues.GetAsync(c.Id, ct)).Select(v => new
            {
                key = v.Key, title = customValues.Fields.FirstOrDefault(f => f.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase))?.Title, value = v.Value, v.UpdatedUtc,
            }),
        };
    }

    [McpServerTool(Name = "computer_software", Title = "Software on a computer", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Programs installed on one computer (name, version, publisher, install date).")]
    public async Task<object> ComputerSoftware([Description("Computer name, DNS name or id.")] string computer,
        [Description("Optional filter on the program name (substring).")] string? filter = null, CancellationToken ct = default)
    {
        var c = await computerService.GetDetailAsync((await caller.ComputerAsync(computer, ct)).Id, ct) ?? throw new McpException("The computer no longer exists.");
        var list = c.Software.Where(s => string.IsNullOrWhiteSpace(filter) || s.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name).Select(s => new { s.Name, s.Version, s.Publisher, InstallDate = Inventory.Core.InventoryDates.Iso(s.InstallDate), s.PerUser });
        return McpCaller.Page(list);
    }

    /// <summary>Mit Rechnernamen werden höchstens so viele Versionen ausgewertet.</summary>
    private const int MaxVersionsWithComputers = 50;

    [McpServerTool(Name = "find_software", Title = "Find software", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which versions of a program are installed and on how many computers. With computers = true also lists the computer names per version.")]
    public async Task<object> FindSoftware([Description("Program name or part of it, e.g. 'Office', '7-Zip', 'Java'.")] string name,
        [Description("Also list the computer names per version.")] bool computers = false, CancellationToken ct = default)
    {
        var matches = (await reports.SoftwareAsync(ct)).Where(s => s.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name).ThenBy(s => s.Version).ToList();
        if (!computers)
            return McpCaller.Page(matches.Select(s => new { s.Name, s.Version, s.Publisher, s.Computers }));
        var keys = matches.Take(MaxVersionsWithComputers).ToList();
        var names = keys.Select(k => k.Name).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Software.AsNoTracking().Where(x => names.Contains(x.Name))
            .Join(db.Computers, x => x.ComputerId, c => c.Id, (x, c) => new { x.Name, x.Version, Computer = c.Name }).ToListAsync(ct);
        var result = keys.Select(s => new
        {
            s.Name, s.Version, s.Publisher, s.Computers,
            computerNames = rows.Where(r => r.Name == s.Name && r.Version == s.Version).Select(r => r.Computer).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList(),
        });
        // total und truncated beziehen sich auf alle Treffer, nicht nur auf die ausgewerteten.
        return new { total = matches.Count, truncated = matches.Count > keys.Count, items = result };
    }

    [McpServerTool(Name = "missing_updates", Title = "Missing updates", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Pending Windows updates. With a computer: its pending updates. Without: every pending update across all computers with the number and names of computers missing it, security updates first.")]
    public async Task<object> MissingUpdates([Description("Computer name or id; empty = overview over all computers.")] string? computer = null,
        [Description("Only security and critical updates.")] bool securityOnly = false, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(computer))
        {
            var c = await computerService.GetDetailAsync((await caller.ComputerAsync(computer, ct)).Id, ct) ?? throw new McpException("The computer no longer exists.");
            return new
            {
                computer = c.Name, c.UpdateLastSearchUtc, c.UpdateRebootRequired,
                updates = c.PendingUpdates.Where(u => !securityOnly || u.IsSecurity).OrderByDescending(u => u.IsSecurity).ThenBy(u => u.Title)
                    .Select(u => new { u.Title, u.KbIds, u.Classification, u.Severity, u.IsSecurity }),
            };
        }
        var compliance = await reports.PatchComplianceAsync(ct);
        return McpCaller.Page(compliance.Updates.Where(u => !securityOnly || u.IsSecurity).OrderByDescending(u => u.IsSecurity).ThenByDescending(u => u.Computers)
            .Select(u => new { u.Title, u.KbIds, u.Classification, u.Severity, u.IsSecurity, u.Computers, computerNames = u.ComputerNames.Take(50) }));
    }

    [McpServerTool(Name = "list_collections", Title = "Collections", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Collections (groups of computers, static or rule-based) with their number of members.")]
    public async Task<object> ListCollections(CancellationToken ct = default) =>
        McpCaller.Page((await collections.ListAsync(ct)).Select(s => new { s.Collection.Id, s.Collection.Name, s.Collection.Description, s.Collection.IsDynamic, members = s.Count }));

    [McpServerTool(Name = "list_packages", Title = "Packages", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Software packages that can be deployed. Operators can only deploy approved ones.")]
    public async Task<object> ListPackages([Description("Optional filter on the name (substring).")] string? filter = null, CancellationToken ct = default) =>
        McpCaller.Page((await packages.ListAsync(ct)).Where(p => string.IsNullOrWhiteSpace(filter) || p.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name).Select(p => new { p.Id, p.Name, p.Version, p.Description, approved = p.IsApproved, steps = p.Steps.Count }));

    [McpServerTool(Name = "list_deployments", Title = "Deployments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Recent deployments, newest first, with counts per outcome.")]
    public async Task<object> ListDeployments([Description("How many (default 20, at most 200).")] int take = 20,
        [Description("true = only deployments that are not finished yet.")] bool runningOnly = false, CancellationToken ct = default) =>
        McpCaller.Page((await deployments.ListAsync(Math.Clamp(take, 1, 200), ct)).Where(d => !runningOnly || d.FinishedUtc is null).Select(d => new
        {
            d.Id, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc, targets = d.Targets.Count,
            succeeded = d.Targets.Count(t => t.Status is TargetStatus.Succeeded or TargetStatus.AlreadyInstalled),
            failed = d.Targets.Count(t => t.Status is TargetStatus.Failed or TargetStatus.Cancelled),
            waiting = d.Targets.Count(t => t.Status.IsWaiting()), d.StopReason,
        }));

    [McpServerTool(Name = "get_deployment", Title = "Deployment details", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Status per computer of one deployment, with errors.")]
    public async Task<object> GetDeployment([Description("Deployment id.")] int id, CancellationToken ct = default)
    {
        var d = await deployments.GetAsync(id, ct) ?? throw new McpException($"Deployment {id} not found.");
        return new
        {
            d.Id, d.PackageName, d.PackageVersion, d.CreatedUtc, d.FinishedUtc, d.StopReason,
            targets = d.Targets.OrderBy(t => t.ComputerName).Select(t => new { t.ComputerName, status = t.Status, t.Error, t.StartedUtc, t.FinishedUtc, t.Pilot }),
        };
    }
}
