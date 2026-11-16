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

/// <summary>Lesende Werkzeuge zu Netzwerkgeräten, IP-Adressen, Virtualisierung und der Microsoft-Lizenzierung; für jede Rolle.</summary>
[McpServerToolType, Authorize(Policy = AccessControl.ViewerPolicy)]
public sealed class NetworkTools(DeviceService devices, IpAddressService ipAddresses, LicensingService licensing, VirtualizationService virtualization,
    HypervisorSyncService hypervisors)
{
    [McpServerTool(Name = "list_devices", Title = "Network devices", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Printers, switches, routers, storage and other devices found by the device scan. "
        + "kind: Printer, Switch, Router, Firewall, AccessPoint, Storage, Linux, Windows, Hypervisor, Camera, Phone, Ups, Management, Iot, Apple, Unknown.")]
    public async Task<object> ListDevices([Description("Only devices of this kind.")] DeviceKind? kind = null,
        [Description("Text in name, IP, model, manufacturer or location.")] string? query = null, CancellationToken ct = default)
    {
        IEnumerable<NetworkDevice> list = await devices.ListAsync(ct);
        if (kind is { } k)
            list = list.Where(d => d.Kind == k);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            list = list.Where(d => new[] { d.DisplayName, d.IpAddress, d.Model, d.Manufacturer, d.MacVendor, d.SysLocation, d.SerialNumber }
                .Any(t => t?.Contains(q, StringComparison.OrdinalIgnoreCase) == true));
        }
        return McpCaller.Page(list.Select(DeviceEndpoints.ToDto));
    }

    [McpServerTool(Name = "list_subnets", Title = "IP subnets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Subnets of the IP address management with the number of addresses per status (in use, DNS only, stale, free, reserved, excluded).")]
    public async Task<object> ListSubnets(CancellationToken ct = default) => McpCaller.Page((await ipAddresses.GetViewsAsync(ct)).Select(IpAddressEndpoints.ToDto));

    /// <summary>Ein ganzes /24 passt in eine Antwort; größere Netze lassen sich über status eingrenzen.</summary>
    public const int MaxAddresses = 256;

    [McpServerTool(Name = "subnet_addresses", Title = "Addresses of a subnet", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every address of a subnet with its status and the evidence behind it (ping, ARP, inventory, DNS records, reservation). "
        + "status: Free, Stale, DnsOnly, InUse, Excluded, Reserved. Stale = only old traces such as an orphaned PTR record. "
        + "At most 256 addresses per call (total/truncated tell whether more exist); narrow larger subnets with status.")]
    public async Task<object> SubnetAddresses([Description("Subnet name, CIDR or id.")] string subnet,
        [Description("Only addresses with this status.")] IpAddressStatus? status = null, CancellationToken ct = default)
    {
        var view = await ViewAsync(ipAddresses, subnet, ct);
        return McpCaller.Page(view.Addresses.Where(a => status is null || a.Status == status).Select(IpAddressEndpoints.ToDto), MaxAddresses);
    }

    internal static async Task<IpSubnetView> ViewAsync(IpAddressService ipAddresses, string subnet, CancellationToken ct)
    {
        var subnets = await ipAddresses.ListSubnetsAsync(ct);
        var match = subnets.FirstOrDefault(s => s.Cidr == subnet.Trim()) ?? McpCaller.Pick(subnets, subnet, s => s.Id, s => s.Name, "subnet");
        return await ipAddresses.GetViewAsync(match.Id, ct) ?? throw new McpException("The subnet no longer exists.");
    }

    [McpServerTool(Name = "list_virtual_hosts", Title = "Virtualization hosts and their VMs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Physical virtualization hosts (ESXi hosts read from vCenter, XCP-ng hosts, inventoried Hyper-V hosts) with cluster, cores, DRS/HA and "
        + "the virtual machines on each, including VMs without an agent such as Linux servers. Answers which host a VM runs on. "
        + "query matches host, cluster or VM name (substring, case-insensitive): a host or cluster match lists all its VMs, otherwise only the matching VMs. "
        + "The data is as of the last sync of each management server (managers: lastSyncUtc, lastResult); with DRS a VM may have moved since. "
        + "No managers = no vCenter or XCP-ng is connected (Virtualization page in the web interface).")]
    public async Task<object> ListVirtualHosts(
        [Description("Host, cluster or VM name (substring); empty = all.")] string? query = null, CancellationToken ct = default)
    {
        var q = query?.Trim();
        bool Match(string? text) => string.IsNullOrEmpty(q) || text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
        var hosts = new List<object>();
        foreach (var host in await virtualization.HostsAsync(ct))
        {
            // Ein Treffer auf Host oder Cluster zeigt den Host auch ohne VMs.
            var hostMatch = Match(host.Name) || Match(host.Cluster);
            var vms = hostMatch ? host.VirtualMachines : host.VirtualMachines.Where(v => Match(v.Name) || Match(v.GuestName)).ToList();
            if (!hostMatch && vms.Count == 0)
                continue;
            hosts.Add(new
            {
                name = host.Name, source = host.Source.ToString(), host.Cluster, managedBy = host.ManagerName, host.CpuSockets, host.CpuCores, host.Model,
                drs = host.ClusterDrs, ha = host.ClusterHa, inventoriedComputerId = host.ComputerId,
                virtualMachines = vms.Select(v => new { v.Name, running = v.Running, v.VirtualCpus, v.GuestOs, inventoriedAs = v.GuestName, computerId = v.GuestComputerId }),
            });
        }
        var unplaced = (await virtualization.GuestsWithoutHostAsync(ct)).Where(c => Match(c.Name)).Select(c => c.Name).ToList();
        return new
        {
            managers = (await hypervisors.ListAsync(ct)).Select(m => new
            {
                m.Name, kind = HypervisorSyncService.Name(m.Kind), m.Enabled,
                lastSyncUtc = m.LastRunUtc is { } run ? DateTime.SpecifyKind(run, DateTimeKind.Utc) : (DateTime?)null, m.LastResult,
            }),
            hosts = McpCaller.Page(hosts),
            // Inventarisierte VMs, deren Host keine Quelle kennt, z. B. eine VMware-VM ohne angebundenes vCenter.
            virtualMachinesWithoutKnownHost = McpCaller.Page(unplaced),
        };
    }

    [McpServerTool(Name = "licensing_summary", Title = "Microsoft licensing", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Estimated Microsoft licence need (Windows Server and SQL Server per core) versus licences owned, per product, plus the data gaps that keep the estimate incomplete. An estimate, not an audit result.")]
    public async Task<object> LicensingSummary(CancellationToken ct = default)
    {
        var overview = await licensing.CalculateAsync(ct);
        return new
        {
            calculatedUtc = DateTime.SpecifyKind(overview.CalculatedUtc, DateTimeKind.Utc),
            balance = overview.Balances.Select(b => new { product = b.Product.Name, unit = b.Product.Unit, need = b.Need, stock = b.Stock, delta = b.Delta, missing = b.Missing,
                versions = b.Versions.Select(v => new { version = v.Version, need = v.Need, stock = v.Stock, missing = v.Missing }) }),
            windowsServerHosts = overview.WindowsServer.Hosts.Count,
            sqlServerEntries = overview.SqlServer.Entries.Count,
            gaps = overview.Gaps.Select(licensing.Format),
        };
    }
}
