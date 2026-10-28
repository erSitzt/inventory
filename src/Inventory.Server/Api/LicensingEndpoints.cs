using System.Globalization;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using Microsoft.Extensions.Localization;
using Inventory.Core;

namespace Inventory.Server.Api;

public sealed record LicenseBalanceDto(string Product, string Name, string Unit, int? Need, int Stock, int? Delta, bool SoftwareAssurance);

public sealed record LicenseVariantDto(string Product, int Cores, decimal Cost, bool Allowed, string? Reason);

/// <param name="VirtualMachines">Bei Windows Server die zu deckenden Windows-Server-VMs, bei SQL Server die SQL-VMs.</param>
/// <param name="VmsOnHost">Die VMs, die gerade auf dem Host laufen (im wandernden Cluster deckt der Host mehr).</param>
/// <param name="Mobile">Die VMs des Clusters wandern zwischen den Hosts.</param>
/// <param name="StillToBuy">Was nach Abzug des Bestands noch zu kaufen ist.</param>
/// <param name="Steps">Der Rechenweg in der Sprache der Anfrage.</param>
public sealed record HostLicensingDto(string Host, string? Cluster, string Source, int LicensedCores, int VirtualMachines, LicenseVariantDto? Chosen,
    IReadOnlyList<LicenseVariantDto> Variants, IReadOnlyList<string> Steps, IReadOnlyList<string> Notes, int VmsOnHost, bool Mobile, decimal? StillToBuy);

public sealed record LicensingDto(string RulesVersion, string SqlRulesVersion, DateTime CalculatedUtc, bool SoftwareAssurance, bool SqlSoftwareAssurance,
    IReadOnlyList<LicenseBalanceDto> Balances, IReadOnlyList<HostLicensingDto> Hosts, IReadOnlyList<HostLicensingDto> SqlServer, IReadOnlyList<string> SqlFree,
    IReadOnlyList<string> Gaps);

public sealed record EntitlementDto(int Id, string Product, int Quantity, string? Version, DateOnly? SoftwareAssuranceUntil, string? ContractNumber, string? Notes);

public sealed record EntitlementRequest(string Product, int Quantity, string? Version = null, DateOnly? SoftwareAssuranceUntil = null, string? ContractNumber = null, string? Notes = null);

public sealed record EntitlementImportDto(int Read, IReadOnlyList<string> Errors, bool Applied);

public static class LicensingEndpoints
{
    public static void MapLicensingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/licensing").WithTags("Microsoft licensing").AddEndpointFilter(ReportEndpoints.MapErrors).WritesRequire(AccessControl.AdminPolicy);

        group.MapGet("", async (LicensingService service, IStringLocalizer<SharedResource> L, CancellationToken ct) =>
        {
            var overview = await service.CalculateAsync(ct);
            string Text(Inventory.Core.Licensing.LicensingNote note) => service.Format(note);
            LicenseVariantDto Variant(Inventory.Core.Licensing.LicenseVariant v) => new(v.Product, v.Cores, v.Cost, v.Allowed, v.Reason is null ? null : Text(v.Reason));
            HostLicensingDto Host(Inventory.Core.Licensing.HostLicensing h) => new(h.Host.Name, h.Host.Cluster, h.Host.Source, h.LicensedCores, h.WindowsVms,
                h.Chosen is null ? null : Variant(h.Chosen), h.Variants.Select(Variant).ToList(), h.Steps.Select(Text).ToList(), h.Notes.Select(Text).ToList(),
                h.VmsOnHost, h.Mobile, h.AdditionalCost);
            return new LicensingDto(overview.Rules.Version, overview.Rules.SqlServer.Version, DateTime.SpecifyKind(overview.CalculatedUtc, DateTimeKind.Utc),
                overview.SoftwareAssurance, overview.SqlSoftwareAssurance,
                overview.Balances.Select(b => new LicenseBalanceDto(b.Product.Key, b.Product.Name, b.Product.Unit, b.Need, b.Stock, b.Delta, b.SoftwareAssurance)).ToList(),
                overview.WindowsServer.Hosts.Select(Host).ToList(), overview.SqlServer.Entries.Select(Host).ToList(), overview.SqlServer.Free.Select(Text).ToList(),
                overview.Gaps.Select(Text).ToList());
        }).WithSummary("Microsoft licence need and stock").WithDescription(
            "Estimate from the inventory: Windows Server per core for every physical host (Hyper-V, vCenter, XCP-ng, physical servers) and SQL Server per core "
            + "(physical servers, per VM or whole host) with the variants considered and the calculation steps, need vs stock per product, free SQL editions "
            + "and the data gaps that keep the need from being complete. need is null for products not calculated yet.");

        group.MapGet("balance.csv", async (LicensingService service, CancellationToken ct) =>
            ReportEndpoints.Csv(ReportExport.Csv(service.BalanceTable(await service.CalculateAsync(ct))), "licensing-balance.csv"))
            .WithSummary("Need vs stock as CSV");

        group.MapGet("hosts.csv", async (LicensingService service, CancellationToken ct) =>
            ReportEndpoints.Csv(ReportExport.Csv(service.HostTable(await service.CalculateAsync(ct))), "licensing-hosts.csv"))
            .WithSummary("Windows Server licensing per host as CSV");

        group.MapGet("report.pdf", async (LicensingService service, SettingsService settings, IStringLocalizer<SharedResource> L, HttpContext http, CancellationToken ct) =>
        {
            var overview = await service.CalculateAsync(ct);
            var branding = await settings.GetReportBrandingAsync(ct);
            var zone = ReportEndpoints.Zone(http) ?? TimeZoneInfo.Local;
            var pdf = ReportPdf.WriteSections(service.ReportSections(overview, await service.EntitlementsAsync(ct)), new ReportPdfOptions(
                L["Microsoft licensing"],
                Meta: L["Rules version {0} · {1}", overview.Rules.Version, TimeZoneInfo.ConvertTimeFromUtc(overview.CalculatedUtc, zone).ToString("g", CultureInfo.CurrentCulture)],
                Description: L["An estimate from the inventory data, not a binding statement for an audit."],
                Organization: branding.Organization, Logo: branding.Logo,
                PageLabel: L["Page {0} of {1}"], TruncatedNote: L["First {0} of {1} rows."], GeneratedBy: L["Created with Inventory"]));
            return Results.File(pdf, "application/pdf", "microsoft-licensing.pdf");
        }).WithSummary("Licensing report as PDF").WithDescription("Summary with need, stock and costs, Windows Server and SQL Server per host with the calculation steps and variants, data gaps, stock and the rules used.");

        group.MapGet("entitlements", async (LicensingService service, CancellationToken ct) => (await service.EntitlementsAsync(ct)).Select(ToDto))
            .WithSummary("Licence stock").WithDescription("Products are the keys from the rules file (see GET /api/licensing, balances[].product). Quantity: cores for core licences, otherwise licences.");

        group.MapPost("entitlements", async (EntitlementRequest request, LicensingService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var saved = await service.SaveEntitlementAsync(ToEntity(0, request), ct);
            await audit.LogAsync(http.Actor(), "Licence stock added via API", $"{saved.Product}: {saved.Quantity}", ct);
            return Results.Created($"/api/licensing/entitlements/{saved.Id}", ToDto(saved));
        }).WithSummary("Add licence stock");

        group.MapPut("entitlements/{id:int}", async (int id, EntitlementRequest request, LicensingService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var saved = await service.SaveEntitlementAsync(ToEntity(id, request), ct);
            await audit.LogAsync(http.Actor(), "Licence stock changed via API", $"{saved.Product}: {saved.Quantity}", ct);
            return Results.Ok(ToDto(saved));
        }).WithSummary("Change licence stock");

        group.MapDelete("entitlements/{id:int}", async (int id, LicensingService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            await service.DeleteEntitlementAsync(id, ct);
            await audit.LogAsync(http.Actor(), "Licence stock deleted via API", $"Entry {id}", ct);
            return Results.NoContent();
        }).WithSummary("Delete licence stock");

        group.MapPost("entitlements/import", async (HttpRequest request, bool? preview, LicensingService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var result = await service.ImportAsync(await reader.ReadToEndAsync(ct), apply: preview != true, ct);
            if (preview != true)
                await audit.LogAsync(http.Actor(), "Licence stock imported via API", $"{result.Rows.Count} entries, {result.Errors.Count} lines skipped", ct);
            return new EntitlementImportDto(result.Rows.Count, result.Errors, preview != true);
        }).Accepts<string>("text/csv").WithSummary("Import licence stock from CSV")
            .WithDescription("Columns: Product, Quantity, Version, SA until, Contract, Notes (separator ; , or tab). Entries are added. ?preview=true only checks the table.");
    }

    private static EntitlementDto ToDto(LicenseEntitlement e) => new(e.Id, e.Product, e.Quantity, e.Version, e.SoftwareAssuranceUntil, e.ContractNumber, e.Notes);

    private static LicenseEntitlement ToEntity(int id, EntitlementRequest r) => new()
    {
        Id = id, Product = r.Product ?? "", Quantity = r.Quantity, Version = r.Version, SoftwareAssuranceUntil = r.SoftwareAssuranceUntil, ContractNumber = r.ContractNumber, Notes = r.Notes,
    };
}
