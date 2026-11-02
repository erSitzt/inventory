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

/// <summary>Lesende Werkzeuge zu Netzwerkgeräten, IP-Adressen und der Microsoft-Lizenzierung; für jede Rolle.</summary>
[McpServerToolType, Authorize(Policy = AccessControl.ViewerPolicy)]
public sealed class NetworkTools(DeviceService devices, IpAddressService ipAddresses, LicensingService licensing)
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
    public async Task<object> ListSubnets(CancellationToken ct = default) => (await ipAddresses.GetViewsAsync(ct)).Select(IpAddressEndpoints.ToDto);

    [McpServerTool(Name = "subnet_addresses", Title = "Addresses of a subnet", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every address of a subnet with its status and the evidence behind it (ping, ARP, inventory, DNS records, reservation). "
        + "status: Free, Stale, DnsOnly, InUse, Excluded, Reserved. Stale = only old traces such as an orphaned PTR record.")]
    public async Task<object> SubnetAddresses([Description("Subnet name, CIDR or id.")] string subnet,
        [Description("Only addresses with this status.")] IpAddressStatus? status = null, CancellationToken ct = default)
    {
        var view = await ViewAsync(ipAddresses, subnet, ct);
        return McpCaller.Page(view.Addresses.Where(a => status is null || a.Status == status).Select(IpAddressEndpoints.ToDto), 1024);
    }

    internal static async Task<IpSubnetView> ViewAsync(IpAddressService ipAddresses, string subnet, CancellationToken ct)
    {
        var subnets = await ipAddresses.ListSubnetsAsync(ct);
        var match = subnets.FirstOrDefault(s => s.Cidr == subnet.Trim()) ?? McpCaller.Pick(subnets, subnet, s => s.Id, s => s.Name, "subnet");
        return await ipAddresses.GetViewAsync(match.Id, ct) ?? throw new McpException("The subnet no longer exists.");
    }

    [McpServerTool(Name = "licensing_summary", Title = "Microsoft licensing", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Estimated Microsoft licence need (Windows Server and SQL Server per core) versus licences owned, per product, plus the data gaps that keep the estimate incomplete. An estimate, not an audit result.")]
    public async Task<object> LicensingSummary(CancellationToken ct = default)
    {
        var overview = await licensing.CalculateAsync(ct);
        return new
        {
            calculatedUtc = DateTime.SpecifyKind(overview.CalculatedUtc, DateTimeKind.Utc),
            balance = overview.Balances.Select(b => new { product = b.Product.Name, unit = b.Product.Unit, need = b.Need, stock = b.Stock, delta = b.Delta }),
            windowsServerHosts = overview.WindowsServer.Hosts.Count,
            sqlServerEntries = overview.SqlServer.Entries.Count,
            gaps = overview.Gaps.Select(licensing.Format),
        };
    }
}
