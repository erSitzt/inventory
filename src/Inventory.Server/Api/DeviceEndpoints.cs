using System.Text;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Components;
using Inventory.Server.Services;

namespace Inventory.Server.Api;

public sealed record DeviceDto(int Id, string IpAddress, string Name, DeviceKind Kind, string? KindReason, string? Manufacturer, string? Model, string? SerialNumber,
    string? Firmware, string? OperatingSystem, string? MacAddress, string? MacVendor, IReadOnlyList<int> OpenPorts, string? Location, string? Contact, long? PageCount,
    IReadOnlyList<DeviceSupplyDto> Supplies, int? ComputerId, string? RangeName, DateTime FirstSeenUtc, DateTime LastSeenUtc);

public sealed record DeviceSupplyDto(string Description, int? Percent);

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("Devices").WritesRequire(AccessControl.OperatorPolicy);

        group.MapGet("", async (DeviceService devices, CancellationToken ct) =>
            (await devices.ListAsync(ct)).Select(ToDto))
            .WithSummary("All network devices").WithDescription("Printers, switches, routers and other devices found by the device scan.");

        group.MapGet("{id:int}", async (int id, DeviceService devices, CancellationToken ct) =>
            await devices.GetAsync(id, ct) is { } device ? Results.Ok(ToDto(device)) : Results.NotFound())
            .WithSummary("One network device");

        group.MapGet("export.csv", async (DeviceService devices, Display display, HttpContext http, CancellationToken ct) =>
            Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(await devices.CsvAsync(display.DeviceKind, ct, ReportEndpoints.Zone(http)))).ToArray(), "text/csv; charset=utf-8", "devices.csv"))
            .WithSummary("Network devices as CSV").Produces<string>(200, "text/csv");

        group.MapPost("scan", async (int? rangeId, DeviceService devices, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await audit.LogAsync(http.Actor(), "Device scan started via API", null, ct);
            // Der Scan läuft im Hintergrund weiter, auch wenn der Aufrufer die Verbindung schließt.
            _ = Task.Run(() => devices.ScanAsync(rangeId, CancellationToken.None), CancellationToken.None);
            return Results.Accepted();
        }).WithSummary("Start a device scan").WithDescription("Scans the given address range or, without rangeId, all enabled ranges. Operators and administrators.");

        return app;
    }

    private static DeviceDto ToDto(NetworkDevice d) => new(d.Id, d.IpAddress, d.DisplayName, d.Kind, d.KindReason, d.Manufacturer, d.Model, d.SerialNumber,
        d.Firmware, d.OsGuess, d.MacAddress, d.MacVendor, [.. d.Ports], d.SysLocation, d.SysContact, d.PageCount,
        [.. d.Supplies.Select(s => new DeviceSupplyDto(s.Description, s.Percent))], d.ComputerId, d.NetworkRange?.Name, d.FirstSeenUtc, d.LastSeenUtc);
}
