using System.Globalization;
using System.Text.Json;
using Inventory.Core;
using Inventory.Core.Licensing;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Bedarf, Bestand und Differenz eines Produkts.</summary>
/// <param name="Need">null = dieses Produkt rechnet die Lizenzberechnung (noch) nicht.</param>
public sealed record ProductBalance(LicenseProduct Product, int? Need, int Stock, bool SoftwareAssurance)
{
    /// <summary>Bestand minus Bedarf; negativ = Unterdeckung.</summary>
    public int? Delta => Need is { } need ? Stock - need : null;
}

public sealed record LicensingOverview(WindowsServerResult WindowsServer, IReadOnlyList<ProductBalance> Balances, IReadOnlyList<LicensingNote> Gaps, MicrosoftRules Rules,
    bool SoftwareAssurance, DateTime CalculatedUtc);

public sealed record LicensingSettings(IReadOnlyDictionary<string, decimal> Prices, bool ClusterWide);

/// <param name="Rows">Gelesene Einträge (noch nicht gespeichert bei der Vorschau).</param>
/// <param name="Errors">Zeilen, die ausgelassen werden.</param>
public sealed record EntitlementImport(List<LicenseEntitlement> Rows, List<string> Errors);

/// <summary>
/// Microsoft-Lizenzbedarf aus den Inventardaten, gegenübergestellt dem eingetragenen Bestand. Eine fundierte Schätzung nach den
/// Regeln aus rules/microsoft, keine verbindliche Auskunft.
/// </summary>
public sealed class LicensingService(IDbContextFactory<InventoryDbContext> dbFactory, SettingsService settings, VirtualizationService virtualization, TimeProvider time,
    IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    public MicrosoftRules Rules => MicrosoftRules.Current;

    public async Task<List<LicenseEntitlement>> EntitlementsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LicenseEntitlements.AsNoTracking().OrderBy(e => e.Product).ThenBy(e => e.ContractNumber).ToListAsync(ct);
    }

    /// <exception cref="ArgumentException">Unbekanntes Produkt oder Menge nicht positiv.</exception>
    public async Task<LicenseEntitlement> SaveEntitlementAsync(LicenseEntitlement entitlement, CancellationToken ct = default)
    {
        Validate(entitlement);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (entitlement.Id == 0)
            db.LicenseEntitlements.Add(entitlement);
        else
        {
            var existing = await db.LicenseEntitlements.FirstOrDefaultAsync(e => e.Id == entitlement.Id, ct) ?? throw new KeyNotFoundException(L["Entry {0} not found.", entitlement.Id]);
            existing.Product = entitlement.Product;
            existing.Quantity = entitlement.Quantity;
            existing.Version = entitlement.Version;
            existing.SoftwareAssuranceUntil = entitlement.SoftwareAssuranceUntil;
            existing.ContractNumber = entitlement.ContractNumber;
            existing.Notes = entitlement.Notes;
            entitlement = existing;
        }
        await db.SaveChangesAsync(ct);
        return entitlement;
    }

    public async Task DeleteEntitlementAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.LicenseEntitlements.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
    }

    private void Validate(LicenseEntitlement entitlement)
    {
        var product = Rules.Product(entitlement.Product) ?? throw new ArgumentException(L["Unknown product \"{0}\".", entitlement.Product]);
        entitlement.Product = product.Key;
        if (entitlement.Quantity <= 0)
            throw new ArgumentException(L["The quantity must be greater than zero."]);
        entitlement.Version = Clean(entitlement.Version);
        entitlement.ContractNumber = Clean(entitlement.ContractNumber);
        entitlement.Notes = Clean(entitlement.Notes);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Spalten der Import-Tabelle; die Reihenfolge ist egal, Produkt und Menge sind Pflicht.</summary>
    public static readonly string[] ImportColumns = ["Product", "Quantity", "Version", "SA until", "Contract", "Notes"];

    /// <summary>
    /// Liest eine Tabelle (z. B. aus dem Lizenzportal oder vom Händler). Produkt als Schlüssel oder Name aus der Regeldatei.
    /// Mit apply = false nur zur Vorschau; mit true werden die gültigen Zeilen hinzugefügt (vorhandene bleiben).
    /// </summary>
    /// <exception cref="ArgumentException">Kopfzeile ohne Produkt oder Menge.</exception>
    public async Task<EntitlementImport> ImportAsync(string csv, bool apply, CancellationToken ct = default)
    {
        var table = AssetCsv.Parse(csv);
        if (table.Count == 0)
            throw new ArgumentException(L["The table is empty."]);
        var header = table[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        int Index(string column) => header.FindIndex(h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase) || string.Equals(h, L[column], StringComparison.OrdinalIgnoreCase));
        var (product, quantity, version, sa, contract, notes) = (Index("Product"), Index("Quantity"), Index("Version"), Index("SA until"), Index("Contract"), Index("Notes"));
        if (product < 0 || quantity < 0)
            throw new ArgumentException(L["The table needs the columns {0} and {1}.", L["Product"], L["Quantity"]]);
        var result = new EntitlementImport([], []);
        foreach (var (row, number) in table.Skip(1).Select((r, i) => (r, i + 2)))
        {
            string? Cell(int index) => index >= 0 && index < row.Count && AssetCsv.Unescape(row[index].Trim()) is { Length: > 0 } text ? text : null;
            var name = Cell(product);
            var match = name is null ? null : Rules.Product(name) ?? Rules.Products.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                result.Errors.Add(L["Line {0}: unknown product \"{1}\".", number, name ?? ""]);
                continue;
            }
            if (!int.TryParse(Cell(quantity), NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                result.Errors.Add(L["Line {0}: the quantity is not a positive whole number.", number]);
                continue;
            }
            DateOnly? until = null;
            if (Cell(sa) is { } saText && (until = AssetCsv.Date(saText)) is null)
            {
                result.Errors.Add(L["Line {0}: \"{1}\" is not a date.", number, saText]);
                continue;
            }
            result.Rows.Add(new LicenseEntitlement
            {
                Product = match.Key, Quantity = amount, Version = Cell(version), SoftwareAssuranceUntil = until, ContractNumber = Cell(contract), Notes = Cell(notes),
            });
        }
        if (apply && result.Rows.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.LicenseEntitlements.AddRange(result.Rows);
            await db.SaveChangesAsync(ct);
        }
        return result;
    }

    public async Task<LicensingSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (await settings.GetAsync(SettingKeys.LicensingPrices, ct) is { Length: > 0 } json)
        {
            try
            {
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, decimal>>(json) ?? [])
                    if (Rules.Product(key) is { } known && value >= 0)
                        prices[known.Key] = value;
            }
            catch (JsonException)
            {
                // Unlesbar: dann gelten die Listenpreise.
            }
        }
        return new LicensingSettings(prices, await settings.GetAsync(SettingKeys.LicensingClusterWide, ct) != bool.FalseString);
    }

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, decimal?> prices, bool clusterWide, CancellationToken ct = default)
    {
        var known = prices.Where(p => p.Value is >= 0 && Rules.Product(p.Key) is not null).ToDictionary(p => Rules.Product(p.Key)!.Key, p => p.Value!.Value);
        await settings.SetAsync(SettingKeys.LicensingPrices, known.Count == 0 ? null : JsonSerializer.Serialize(known), ct);
        await settings.SetAsync(SettingKeys.LicensingClusterWide, clusterWide.ToString(), ct);
    }

    /// <summary>Rechnet den Bedarf aus dem aktuellen Inventar und stellt ihn dem Bestand gegenüber.</summary>
    public async Task<LicensingOverview> CalculateAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var entitlements = await EntitlementsAsync(ct);
        var options = await GetSettingsAsync(ct);
        var windowsServerProducts = new[] { MicrosoftRules.WindowsServerStandard, MicrosoftRules.WindowsServerDatacenter, MicrosoftRules.WindowsServerPerVm };
        var softwareAssurance = entitlements.Any(e => windowsServerProducts.Contains(e.Product) && e.SoftwareAssuranceActive(today));
        var (hosts, gaps) = await HostsAsync(ct);
        var stock = entitlements.GroupBy(e => e.Product).ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity));
        var result = WindowsServerEngine.Calculate(hosts, new WindowsServerOptions(options.Prices, options.ClusterWide, softwareAssurance, stock), Rules);

        var balances = Rules.Products.Select(product =>
        {
            var stock = entitlements.Where(e => e.Product == product.Key).Sum(e => e.Quantity);
            var sa = entitlements.Any(e => e.Product == product.Key && e.SoftwareAssuranceActive(today));
            int? need = windowsServerProducts.Contains(product.Key) ? result.Need.GetValueOrDefault(product.Key) : null;
            return new ProductBalance(product, need, stock, sa);
        }).ToList();
        foreach (var expired in entitlements.Where(e => e.SoftwareAssuranceUntil is { } until && until < today))
            gaps.Add(new("Software Assurance of {0} ({1}) ended on {2}.", Rules.Product(expired.Product)?.Name ?? expired.Product, expired.ContractNumber ?? "–",
                expired.SoftwareAssuranceUntil!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return new LicensingOverview(result, balances, [.. gaps, .. result.Gaps], Rules, softwareAssurance, time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Die physischen Server: Hyper-V- und ESXi-Hosts mit ihren VMs, dazu Windows Server ohne Virtualisierung. VMs ohne bekannten Host
    /// werden als Lücke gemeldet.
    /// </summary>
    private async Task<(List<LicensingHost> Hosts, List<LicensingNote> Gaps)> HostsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        bool? WindowsServer(int? computerId, string? guestOs)
        {
            var os = computerId is { } id && computers.TryGetValue(id, out var computer) && computer.OsName is { } name ? name : guestOs;
            return os is null ? null : os.Contains("Windows", StringComparison.OrdinalIgnoreCase) && os.Contains("Server", StringComparison.OrdinalIgnoreCase);
        }

        var hosts = new List<LicensingHost>();
        var hostComputers = new HashSet<int>();
        foreach (var view in await virtualization.HostsAsync(ct))
        {
            var vms = view.VirtualMachines.Select(v => new LicensingVm(v.Name, v.VirtualCpus, v.Running ?? true, WindowsServer(v.GuestComputerId, v.GuestOs), v.GuestComputerId)).ToList();
            if (view.Source == VirtualHostSource.HyperV && view.ComputerId is { } hostId && computers.TryGetValue(hostId, out var host))
            {
                hostComputers.Add(hostId);
                hosts.Add(new LicensingHost($"computer:{hostId}", host.Name, host.ClusterName, "Hyper-V", host.CpuSockets, host.CpuCoresPerSocket, host.CpuCores,
                    true, host.OsEdition, hostId, vms));
            }
            else
                hosts.Add(new LicensingHost($"hypervisor:{view.Id}", view.Name, view.Cluster, view.Source switch { VirtualHostSource.XcpNg => "XCP-ng", VirtualHostSource.XenOrchestra => "Xen Orchestra", _ => "vCenter" },
                    view.CpuSockets, null, view.CpuCores, false, null, null, vms));
        }

        var gaps = new List<LicensingNote>();
        foreach (var computer in computers.Values.Where(c => IsWindowsServer(c) && !hostComputers.Contains(c.Id)).OrderBy(c => c.Name))
        {
            if (computer.IsVirtual)
            {
                if (computer.HostComputerId is null && computer.HypervisorHostId is null)
                    gaps.Add(new("{0}: virtual machine without a known host; it is not included.", computer.Name));
                continue;
            }
            hosts.Add(new LicensingHost($"computer:{computer.Id}", computer.Name, computer.ClusterName, "physical", computer.CpuSockets, computer.CpuCoresPerSocket, computer.CpuCores,
                true, computer.OsEdition, computer.Id, []));
        }
        return (hosts, gaps);
    }

    private static bool IsWindowsServer(Computer computer) =>
        computer.OsProductType is 2 or 3 || (computer.OsName ?? "").Contains("Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bedarf, Bestand und Differenz als Tabelle für CSV, Excel und PDF.</summary>
    public ReportTable BalanceTable(LicensingOverview overview) => new(
        [
            new("product", L["Product"]), new("need", L["Need"], true), new("stock", L["Stock"], true), new("delta", L["Difference"], true), new("status", L["Status"]),
        ],
        overview.Balances.Where(b => b.Need is not null || b.Stock > 0).Select(b => new string?[]
        {
            L[b.Product.Name], b.Need?.ToString(CultureInfo.InvariantCulture), b.Stock.ToString(CultureInfo.InvariantCulture),
            b.Delta?.ToString(CultureInfo.InvariantCulture), Status(b),
        }).ToList());

    /// <summary>Je Host die gewählte Variante, mit den Kernen und den berücksichtigten VMs.</summary>
    public ReportTable HostTable(LicensingOverview overview) => new(
        [
            new("host", L["Host"]), new("cluster", L["Cluster"]), new("source", L["Source"]), new("cores", L["Licensed cores"], true),
            new("vms", L["Windows Server VMs"], true), new("product", L["Product"]), new("licences", L["Cores to license"], true),
        ],
        overview.WindowsServer.Hosts.Where(h => h.Chosen is not null).Select(h => new string?[]
        {
            h.Host.Name, h.Host.Cluster, h.Host.Source, h.LicensedCores.ToString(CultureInfo.InvariantCulture), h.WindowsVms.ToString(CultureInfo.InvariantCulture),
            L[overview.Rules.Product(h.Chosen!.Product)?.Name ?? h.Chosen.Product], h.Chosen.Cores.ToString(CultureInfo.InvariantCulture),
        }).ToList());

    public string Status(ProductBalance balance) => balance.Delta switch
    {
        null => L["not calculated"],
        < 0 => L["shortfall"],
        0 => L["covered"],
        _ => L["surplus"],
    };
}
