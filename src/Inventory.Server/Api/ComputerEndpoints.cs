using Inventory.Server.Services;
using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Api;

public static class ComputerEndpoints
{
    public sealed record ComputerDto(int Id, string Name, string? Description, string? IpAddress, bool IsOnline,
        DateTime? LastSeenUtc, DateTime? LastScanUtc, string? LastScanError, TransportKind? Transport, int? CredentialId,
        string? ReportedName, string? ReverseDnsName, bool NameMismatch, string? OsName, string? OsVersion, string? Manufacturer, string? Model, string? SerialNumber, long? MemoryBytes, string? LoggedOnUser,
        string? PrimaryUser = null, string? Owner = null, string? Location = null, string? CostCenter = null, string? InventoryNumber = null,
        DateOnly? PurchaseDate = null, DateOnly? WarrantyUntil = null, string? Tags = null, string? Notes = null);

    /// <summary>Jedes Feld ist optional: weggelassen oder null = unverändert, leerer Text = löschen. Die Datumsfelder löscht <c>Clear</c>.</summary>
    public sealed record AssetRequest(string? Description = null, string? Owner = null, string? Location = null, string? CostCenter = null, string? InventoryNumber = null,
        DateOnly? PurchaseDate = null, DateOnly? WarrantyUntil = null, string? Tags = null, string? Notes = null, string[]? Clear = null);

    public sealed record HardwareDto(string DeviceType, string? FirmwareType, bool? SecureBoot, DateTime? BiosDateUtc, bool? TpmPresent, string? TpmVersion, bool? TpmReady,
        string? Gpu, int? MemorySlots, bool? BatteryPresent, int? BatteryHealthPercent, int? BatteryCycleCount,
        IEnumerable<MemoryModuleDto> MemoryModules, IEnumerable<PhysicalDiskDto> PhysicalDisks, IEnumerable<VolumeDto> Volumes,
        IEnumerable<MonitorDto> Monitors, IEnumerable<PrinterDto> Printers);

    public sealed record MemoryModuleDto(string Slot, string? Manufacturer, string? PartNumber, string? SerialNumber, long CapacityBytes, int? SpeedMhz, string? Type);

    public sealed record PhysicalDiskDto(string Model, string? SerialNumber, string? MediaType, string? BusType, long SizeBytes, string? Health);

    public sealed record VolumeDto(string Drive, bool? Protected, int? EncryptedPercent, string? Method);

    public sealed record MonitorDto(string? Manufacturer, string Model, string? SerialNumber, int? Year);

    public sealed record PrinterDto(string Name, string? Driver, string? Port, bool Network, bool Shared);

    public sealed record PendingUpdateDto(string Title, string? KbIds, string? Classification, string? Severity, bool IsSecurity, bool IsDownloaded, long SizeBytes);

    public sealed record ImportRequest(string Names);

    public sealed record ProbeResponse(int Online);

    public sealed record SoftwareDto(string Name, string? Version, string? Publisher, string? InstallDate, bool Is64Bit, bool PerUser, bool CarriedOver);

    public sealed record RunRequest(StepType Type, string Script, int? TimeoutSeconds, StepRunAs RunAs = StepRunAs.System);

    public static IEndpointRouteBuilder MapComputerEndpoints(this IEndpointRouteBuilder app)
    {
        // Starter für Admin-PCs: öffnet Fernzugriffe direkt aus der Oberfläche (inventory://-Links).
        app.MapGet("/download/launcher", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "launcher", "InventoryLauncher.exe");
            return File.Exists(path) ? Results.File(path, "application/octet-stream", "InventoryLauncher.exe") : Results.NotFound();
        }).WithTags("Downloads").WithSummary("Launcher for admin PCs").WithDescription("InventoryLauncher.exe: registers inventory:// links through which the UI opens Remote Assistance, RDP and RDP shadowing directly.");

        var group = app.MapGroup("/api/computers").WithTags("Computers");

        group.MapGet("", async (ComputerService computers, CancellationToken ct) =>
            (await computers.ListAsync(ct)).Select(ToDto))
            .WithSummary("All computers").WithDescription("Master data, status and the most important inventory values of all computers.");

        group.MapGet("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetAsync(id, ct) is { } computer ? Results.Ok(ToDto(computer)) : Results.NotFound())
            .WithSummary("One computer").Produces<ComputerDto>().Produces(404);

        group.MapPost("", async (ImportRequest request, ComputerService computers, CancellationToken ct) =>
            (await computers.ImportAsync(request.Names, ct)).Select(ToDto))
            .RequireAdmin().WithSummary("Create computers").WithDescription("Names separated by newline, comma, semicolon or space. Returns only the newly created computers; existing ones stay unchanged.");

        group.MapPatch("{id:int}", async (int id, AssetRequest request, ComputerService computers, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await computers.GetAsync(id, ct) is not { } computer)
                return Results.NotFound();
            bool Cleared(string field) => request.Clear?.Contains(field, StringComparer.OrdinalIgnoreCase) == true;
            var asset = computer.Asset;
            await computers.UpdateAssetAsync(id, new AssetData(request.Owner ?? asset.Owner, request.Location ?? asset.Location, request.CostCenter ?? asset.CostCenter,
                request.InventoryNumber ?? asset.InventoryNumber,
                Cleared("purchaseDate") ? null : request.PurchaseDate ?? asset.PurchaseDate, Cleared("warrantyUntil") ? null : request.WarrantyUntil ?? asset.WarrantyUntil,
                request.Tags ?? asset.Tags, request.Notes ?? asset.Notes), ct);
            if (request.Description is not null)
                await computers.UpdateAsync(id, request.Description, computer.Transport, computer.CredentialId, ct);
            await audit.LogAsync(http.Actor(), "Computer changed via API", computer.Name, ct);
            return Results.Ok(ToDto((await computers.GetAsync(id, ct))!));
        }).RequireAdmin().WithSummary("Change asset data").WithDescription("Sets description, owner, location, cost center, inventory number, purchase date, end of warranty, tags (comma-separated) and notes. Omitted fields stay unchanged; an empty string clears a text field; clear = [\"purchaseDate\", \"warrantyUntil\"] clears the dates.").Produces<ComputerDto>().Produces(404);

        group.MapPost("assets/import", async (HttpContext http, ComputerService computers, AuditService audit, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            try
            {
                var result = await computers.ImportAssetsAsync(await reader.ReadToEndAsync(ct), ct);
                if (result.Updated.Count > 0)
                    await audit.LogAsync(http.Actor(), "Asset data imported via API", string.Join(", ", result.Updated), ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        }).RequireAdmin().WithSummary("Import asset data").WithDescription("Body: CSV text (semicolon, comma or tab; first row = column names as in the computer CSV export or in English: Name, Owner, Location, Cost center, Inventory number, Purchase date, Warranty until, Tags, Notes, Description). An empty cell leaves the value unchanged, \"-\" clears it.").Accepts<string>("text/csv").Produces<AssetImportResult>().Produces(400);

        group.MapDelete("{id:int}", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.DeleteAsync([id], ct) > 0 ? Results.NoContent() : Results.NotFound())
            .RequireAdmin().WithSummary("Delete computer").WithDescription("Deletes the computer including its inventory data and history.").Produces(204).Produces(404);

        group.MapPost("probe", async (int[]? ids, ComputerService computers, CancellationToken ct) =>
            new ProbeResponse(await computers.ProbeAsync(ids is { Length: > 0 } ? ids : null, ct)))
            .RequireOperator().WithSummary("Check reachability").WithDescription("Body: JSON array of computer IDs; empty or null = all computers. Returns the number of reachable computers.");

        group.MapPost("{id:int}/scan", async (int id, ScanService scans, CancellationToken ct) => await scans.ScanAsync(id, ct))
            .RequireOperator().WithSummary("Inventory scan").WithDescription("Scans the computer immediately and waits for the result (seconds to minutes depending on the transport).");

        group.MapGet("{id:int}/software", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetDetailAsync(id, ct) is { } computer
                ? Results.Ok(computer.Software.OrderBy(s => s.Name).Select(s => new SoftwareDto(s.Name, s.Version, s.Publisher, s.InstallDate, s.Is64Bit, s.PerUser, s.CarriedOver)))
                : Results.NotFound())
            .WithSummary("Installed software").WithDescription("perUser = installed in one user's profile only. carriedOver = kept from an earlier scan because that user was not logged on during the last scan.").Produces<IEnumerable<SoftwareDto>>().Produces(404);

        group.MapGet("{id:int}/updates", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetDetailAsync(id, ct) is { } c
                ? Results.Ok(c.PendingUpdates.OrderBy(u => u.Title).Select(u => new PendingUpdateDto(u.Title, u.KbIds, u.Classification, u.Severity, u.IsSecurity, u.IsDownloaded, u.SizeBytes)))
                : Results.NotFound())
            .WithSummary("Pending updates").WithDescription("Updates not yet installed, as of the computer's last own update search; the scan does not trigger a search.").Produces<IEnumerable<PendingUpdateDto>>().Produces(404);

        group.MapGet("{id:int}/hardware", async (int id, ComputerService computers, CancellationToken ct) =>
            await computers.GetDetailAsync(id, ct) is { } c
                ? Results.Ok(new HardwareDto(Chassis.Kind(c.ChassisType, c.Model).ToString(), c.FirmwareType, c.SecureBoot, c.BiosDateUtc, c.TpmPresent, c.TpmVersion, c.TpmReady,
                    c.Gpu, c.MemorySlots, c.BatteryPresent, c.BatteryHealthPercent, c.BatteryCycleCount,
                    c.MemoryModules.Select(m => new MemoryModuleDto(m.Slot, m.Manufacturer, m.PartNumber, m.SerialNumber, m.CapacityBytes, m.SpeedMhz, m.Type)),
                    c.PhysicalDisks.Select(d => new PhysicalDiskDto(d.Model, d.SerialNumber, d.MediaType, d.BusType, d.SizeBytes, d.Health)),
                    c.Volumes.Select(v => new VolumeDto(v.Drive, v.Protected, v.EncryptedPercent, v.Method)),
                    c.Monitors.Select(m => new MonitorDto(m.Manufacturer, m.Model, m.SerialNumber, m.Year)),
                    c.Printers.Select(p => new PrinterDto(p.Name, p.Driver, p.Port, p.Network, p.Shared))))
                : Results.NotFound())
            .WithSummary("Hardware details").WithDescription("Chassis, firmware, Secure Boot, TPM, BitLocker per volume, memory modules, physical disks, monitors and printers from the last scan. deviceType: Unknown, Desktop, Laptop, Tablet, Server or VirtualMachine. Values are null when they could not be determined.").Produces<HardwareDto>().Produces(404);

        // Lädt eine .rdp-Datei; Windows öffnet sie direkt mit der Remotedesktopverbindung.
        group.MapGet("{id:int}/rdp", async (int id, ComputerService computers, AuditService audit, HttpContext http, IStringLocalizer<SharedResource> L, CancellationToken ct) =>
        {
            if (await computers.GetAsync(id, ct) is not { } computer)
                return Results.NotFound();
            await audit.LogAsync(http.Actor(), L["Remote access: Remote Desktop"], computer.Name, ct);
            return Results.File(System.Text.Encoding.ASCII.GetBytes(RemoteAccessLinks.RdpFile(computer.Name)), "application/x-rdp", $"{computer.Name}.rdp");
        }).RequireOperator().WithSummary("RDP file").WithDescription("Connection file for Remote Desktop Connection; the download is logged.").Produces(404);

        group.MapPost("{id:int}/run", async (int id, RunRequest request, RemoteJobService jobs, AuditService audit, HttpContext http, IStringLocalizer<SharedResource> L, CancellationToken ct) =>
        {
            await audit.LogAsync(http.Actor(), L["Remote command via API ({0})", request.Type], $"{L["Computer"]} {id}: {request.Script}", ct);
            var job = new JobManifest
            {
                Steps = [new JobStep { Name = L["Command"], Type = request.Type, Script = request.Script, TimeoutSeconds = request.TimeoutSeconds ?? 600, RunAs = request.RunAs }],
            };
            return await jobs.RunAsync(id, job, null, ct);
        }).RequireAdmin().WithSummary("Run command").WithDescription("Runs a command (Type = Command) or PowerShell script (Type = PowerShell) on the computer and waits for the result. RunAs: System (default) or LoggedOnUser. Logged.");

        return app;
    }

    private static ComputerDto ToDto(Computer c) =>
        new(c.Id, c.Name, c.Description, c.IpAddress, c.IsOnline, c.LastSeenUtc, c.LastScanUtc, c.LastScanError, c.Transport, c.CredentialId,
            c.ReportedName, c.ReverseDnsName, c.NameMismatch, c.OsName, c.OsVersion, c.Manufacturer, c.Model, c.SerialNumber, c.MemoryBytes, c.LoggedOnUser,
            c.PrimaryUser, c.Owner, c.Location, c.CostCenter, c.InventoryNumber, c.PurchaseDate, c.WarrantyUntil, c.Tags, c.Notes);
}
