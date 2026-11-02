using System.ComponentModel;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Api;
using Inventory.Server.Services;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Inventory.Server.Mcp;

/// <summary>
/// Handelnde Werkzeuge für Operatoren und Administratoren: scannen, wecken, verteilen, IP-Adressen reservieren, Protokoll.
/// Was nur Administratoren dürfen (Pakete ändern, Fernbefehle, Zugangsdaten, Einstellungen), gibt es hier bewusst nicht.
/// </summary>
[McpServerToolType, Authorize(Policy = AccessControl.OperatorPolicy)]
public sealed class OperatorTools(McpCaller caller, ComputerService computerService, ScanService scans, WakeService wake, CollectionService collections, PackageService packages,
    DeploymentService deployments, IpAddressService ipAddresses, DeviceService devices, AuditService audit)
{
    /// <summary>Ein Scan wartet auf jeden Rechner; mehr würde den Aufruf minutenlang blockieren.</summary>
    public const int MaxScan = 20;

    [McpServerTool(Name = "scan_computers", Title = "Scan computers", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Runs an inventory scan on the computers now and waits for the result (seconds to minutes each). At most 20 per call.")]
    public async Task<object> ScanComputers([Description("Computer names or ids.")] string[] computers, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        if (computers.Length > MaxScan)
            throw new McpException($"At most {MaxScan} computers per call.");
        var targets = await caller.ComputersAsync(computers, ct);
        await caller.RequireScopeAsync(targets.Select(c => c.Id), ct);
        await caller.LogAsync("Computer scan started via MCP", string.Join(", ", targets.Select(c => c.Name)), ct);
        var outcomes = await scans.ScanAsync(targets.Select(c => c.Id), ct: ct);
        var names = targets.ToDictionary(c => c.Id, c => c.Name);
        return outcomes.Select(o => new { computer = names.GetValueOrDefault(o.ComputerId), o.Success, o.Error });
    }

    [McpServerTool(Name = "check_reachability", Title = "Check reachability", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Checks whether the computers answer on the network and updates their online status.")]
    public async Task<object> CheckReachability([Description("Computer names or ids.")] string[] computers, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var targets = await caller.ComputersAsync(computers, ct);
        await caller.RequireScopeAsync(targets.Select(c => c.Id), ct);
        await computerService.ProbeAsync(targets.Select(c => c.Id), ct);
        await caller.LogAsync("Reachability checked via MCP", string.Join(", ", targets.Select(c => c.Name)), ct);
        var ids = targets.Select(c => c.Id).ToHashSet();
        return (await computerService.ListAsync(ct)).Where(c => ids.Contains(c.Id)).Select(c => new { c.Name, c.IsOnline, c.IpAddress, c.LastSeenUtc });
    }

    [McpServerTool(Name = "wake_computers", Title = "Wake computers", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Sends Wake-on-LAN to the computers (from the server and through agents in their subnet). Sent does not mean awake: check reachability about two minutes later.")]
    public async Task<object> WakeComputers([Description("Computer names or ids.")] string[] computers, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var targets = await caller.ComputersAsync(computers, ct);
        await caller.RequireScopeAsync(targets.Select(c => c.Id), ct);
        var results = await wake.WakeAsync(targets.Select(c => c.Id).ToList(), ct);
        if (results.Any(r => r.Sent))
            await caller.LogAsync("Wake-up sent via MCP", string.Join(", ", results.Where(r => r.Sent).Select(r => r.ComputerName)), ct);
        return results.Select(r => new { computer = r.ComputerName, r.Sent, r.Macs, r.Relays });
    }

    [McpServerTool(Name = "deploy_package", Title = "Deploy a package", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Installs an approved package on computers (or on all members of a collection) and returns at once; follow the progress with get_deployment. "
        + "Operators can only deploy approved packages. The computers are scanned again afterwards.")]
    public async Task<object> DeployPackage(
        [Description("Package name or id.")] string package,
        [Description("Computer names or ids. Either this or collection.")] string[]? computers = null,
        [Description("Collection name or id; all its current members. Either this or computers.")] string? collection = null,
        [Description("Retry unreachable computers for up to 3 days.")] bool retryOffline = false,
        [Description("Computers outside their maintenance window wait until it opens.")] bool respectMaintenanceWindow = false,
        CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        if (!string.IsNullOrWhiteSpace(collection) && computers is { Length: > 0 })
            throw new McpException("Give either computers or a collection, not both.");
        var target = McpCaller.Pick(await packages.ListAsync(ct), package, p => p.Id, p => p.Name, "package");
        List<int> ids;
        if (!string.IsNullOrWhiteSpace(collection))
        {
            var match = McpCaller.Pick(await collections.ListAsync(ct), collection, s => s.Collection.Id, s => s.Collection.Name, "collection");
            ids = [.. await collections.GetMemberIdsAsync(match.Collection.Id, ct)];
        }
        else
        {
            ids = [.. (await caller.ComputersAsync(computers ?? [], ct)).Select(c => c.Id)];
        }
        if (ids.Count == 0)
            throw new McpException("No computers to deploy to.");
        await caller.RequireScopeAsync(ids, ct);
        try
        {
            var deployment = await deployments.StartAsync(target.Id, ids, new DeploymentOptions(ScanAfter: true, retryOffline, respectMaintenanceWindow)
            {
                // Wie in der API: nur Administratoren dürfen ein noch nicht freigegebenes Paket verteilen.
                RequireApproved = !await caller.IsAdminAsync(),
            }, ct);
            await caller.LogAsync("Deployment started via MCP", $"#{deployment.Id} {deployment.PackageName} on {string.Join(", ", deployment.Targets.Select(t => t.ComputerName))}", ct);
            return new { deployment.Id, deployment.PackageName, deployment.PackageVersion, computers = deployment.Targets.Select(t => t.ComputerName) };
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "cancel_deployment", Title = "Cancel a deployment", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Stops the running computers of a deployment and drops those still waiting.")]
    public async Task<string> CancelDeployment([Description("Deployment id.")] int id, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var deployment = await deployments.GetAsync(id, ct) ?? throw new McpException($"Deployment {id} not found.");
        await caller.RequireScopeAsync(deployment.Targets.Where(t => t.ComputerId is not null).Select(t => t.ComputerId!.Value), ct);
        await deployments.CancelAsync(id);
        await caller.LogAsync("Deployment cancelled via MCP", $"#{id} {deployment.PackageName}", ct);
        return $"Deployment #{id} ({deployment.PackageName}) cancelled.";
    }

    [McpServerTool(Name = "find_free_ips", Title = "Find free IP addresses", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Finds free addresses in a subnet and pings each candidate again before returning it; candidates that answer are skipped and recorded as in use. "
        + "Does not reserve them: use reserve_ips for that. Empty when not enough are free.")]
    public async Task<object> FindFreeIps(
        [Description("Subnet name, CIDR or id.")] string subnet,
        [Description("How many addresses (1 to 256).")] int count = 1,
        [Description("All addresses in one contiguous block.")] bool contiguous = false,
        [Description("Search from the highest address down.")] bool fromTop = false,
        [Description("Also offer stale addresses (only old DNS entries or not seen for 30 days).")] bool includeStale = false,
        CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var view = await NetworkTools.ViewAsync(ipAddresses, subnet, ct);
        try
        {
            var result = await ipAddresses.FindFreeAsync(view.Subnet.Id, count, contiguous, includeStale, fromTop, ct);
            // Die Suche vermerkt Kandidaten, die geantwortet haben, als belegt; deshalb steht sie im Protokoll.
            await caller.LogAsync("Free IP addresses searched via MCP",
                $"{view.Subnet.Name}: {(result.Addresses.Count == 0 ? "none" : string.Join(", ", result.Addresses))}{(result.Answered.Count > 0 ? $"; answered: {string.Join(", ", result.Answered)}" : "")}", ct);
            return new { subnet = view.Subnet.Name, view.Subnet.Cidr, addresses = result.Addresses, answeredNow = result.Answered };
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "reserve_ips", Title = "Reserve IP addresses", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Reserves addresses in a subnet so nobody else takes them: either the given addresses, or count free ones found (and checked by ping) right now. "
        + "Reserved addresses are never offered as free. Give a note saying what they are for.")]
    public async Task<object> ReserveIps(
        [Description("Subnet name, CIDR or id.")] string subnet,
        [Description("What the addresses are for, e.g. 'new SQL server, ticket 4711'.")] string note,
        [Description("Specific addresses to reserve. Leave empty to have count free ones found.")] string[]? addresses = null,
        [Description("How many free addresses to find and reserve when no addresses are given.")] int count = 1,
        [Description("Find one contiguous block when no addresses are given.")] bool contiguous = false,
        [Description("Release automatically after this date (yyyy-MM-dd); empty = until released by hand.")] DateOnly? until = null,
        CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var view = await NetworkTools.ViewAsync(ipAddresses, subnet, ct);
        DateTime? expires = until is { } day ? day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime() : null;
        var user = caller.User.Identity?.Name;
        try
        {
            var reserved = addresses is { Length: > 0 }
                ? await ipAddresses.ReserveAsync(view.Subnet.Id, addresses, note, expires, user, ct)
                : await ipAddresses.ReserveFreeAsync(view.Subnet.Id, count, contiguous, note, expires, user, ct);
            await caller.LogAsync("IP addresses reserved via MCP", IpAddressEndpoints.Details(reserved), ct);
            return reserved.Select(IpAddressEndpoints.ToDto);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "release_ip_reservation", Title = "Release an IP reservation", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Releases the reservation of an address so it can be offered as free again.")]
    public async Task<string> ReleaseIpReservation([Description("The reserved IP address.")] string address, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        foreach (var subnet in await ipAddresses.ListSubnetsAsync(ct))
        {
            if (!IpRanges.Contains(subnet.Cidr, address.Trim()))
                continue;
            var reservation = (await ipAddresses.ListReservationsAsync(subnet.Id, ct)).FirstOrDefault(r => r.Address == address.Trim())
                ?? throw new McpException($"{address} is not reserved.");
            await ipAddresses.ReleaseAsync(reservation.Id, ct);
            await caller.LogAsync("IP reservation released via MCP", IpAddressEndpoints.Details([reservation]), ct);
            return $"Reservation of {reservation.Address} released.";
        }
        throw new McpException($"{address} is in none of the subnets.");
    }

    [McpServerTool(Name = "start_ip_scan", Title = "Scan IP subnets", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts a scan of one subnet (or all) in the background: ping, ARP and DNS. Takes seconds to a few minutes; check list_subnets afterwards.")]
    public async Task<string> StartIpScan([Description("Subnet name, CIDR or id; empty = all subnets.")] string? subnet = null, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        if (ipAddresses.Current is not null)
            return "An IP address scan is already running.";
        int? id = string.IsNullOrWhiteSpace(subnet) ? null : (await NetworkTools.ViewAsync(ipAddresses, subnet, ct)).Subnet.Id;
        await caller.LogAsync("IP address scan started via MCP", subnet, ct);
        _ = Task.Run(() => ipAddresses.ScanAsync(id, CancellationToken.None), CancellationToken.None);
        return "IP address scan started.";
    }

    [McpServerTool(Name = "start_device_scan", Title = "Scan for network devices", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts the device scan of one address range (or all enabled ones) in the background. Takes minutes; check list_devices afterwards.")]
    public async Task<string> StartDeviceScan([Description("Range name or id; empty = all enabled ranges.")] string? range = null, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        if (devices.Current is not null)
            return "A device scan is already running.";
        int? id = string.IsNullOrWhiteSpace(range) ? null : McpCaller.Pick(await devices.ListRangesAsync(ct), range, r => r.Id, r => r.Name, "address range").Id;
        await caller.LogAsync("Device scan started via MCP", range, ct);
        _ = Task.Run(() => devices.ScanAsync(id, CancellationToken.None), CancellationToken.None);
        return "Device scan started.";
    }

    [McpServerTool(Name = "audit_log", Title = "Audit log", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Who did what and when (sign-ins, deployments, changes), newest first.")]
    public async Task<object> AuditLog(
        [Description("Text in user, action or details.")] string? search = null,
        [Description("Only the last n days (default 7).")] int days = 7,
        [Description("How many entries (default 100, at most 500).")] int take = 100,
        CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var entries = await audit.ListAsync(Math.Clamp(take, 1, 500), ct, new AuditFilter(FromUtc: DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 3650)), Search: search));
        return entries.Select(e => new { timeUtc = DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc), e.User, e.Role, e.Source, e.Action, e.Details });
    }
}
