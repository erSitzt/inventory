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
/// <param name="UnitPrice">Preis je Kern bzw. je Lizenz (eigener Preis oder Listenpreis); null, wenn keiner bekannt ist.</param>
public sealed record ProductBalance(LicenseProduct Product, int? Need, int Stock, bool SoftwareAssurance, decimal? UnitPrice = null)
{
    /// <summary>Bestand minus Bedarf; negativ = Unterdeckung.</summary>
    public int? Delta => Need is { } need ? Stock - need : null;

    /// <summary>Was der Bedarf kostet.</summary>
    public decimal? NeedCost => Need is { } need && UnitPrice is { } price ? need * price : null;

    /// <summary>Was die Unterdeckung kostet, also noch zu kaufen ist; 0 bei Deckung.</summary>
    public decimal? ShortfallCost => Delta is { } delta && UnitPrice is { } price ? Math.Max(0, -delta) * price : null;
}

/// <param name="SoftwareAssurance">SA bzw. Abonnement für Windows Server.</param>
/// <param name="SqlSoftwareAssurance">SA bzw. Abonnement für SQL Server.</param>
/// <param name="Clusters">Die Cluster mit VMs und ob deren VMs zwischen den Hosts wandern.</param>
public sealed record LicensingOverview(WindowsServerResult WindowsServer, SqlServerResult SqlServer, IReadOnlyList<ProductBalance> Balances, IReadOnlyList<LicensingNote> Gaps,
    MicrosoftRules Rules, bool SoftwareAssurance, bool SqlSoftwareAssurance, IReadOnlyDictionary<string, decimal> Prices, DateTime CalculatedUtc)
{
    public IReadOnlyList<LicensingCluster> Clusters { get; init; } = [];
}

/// <summary>Ein Cluster bzw. Pool für die Lizenzierung.</summary>
/// <param name="Key">Schlüssel wie <see cref="LicensingHost.ClusterKey"/>; unter ihm wird die Einstellung gespeichert.</param>
/// <param name="Mobile">Die VMs wandern zwischen den Hosts (so wird gerechnet).</param>
/// <param name="Setting">Für diesen Cluster eingestellt; null = automatisch (erkannt oder Voreinstellung).</param>
/// <param name="Licensing">Die gewählte Art: <see cref="LicenseVariant.PlanHost"/>, <see cref="LicenseVariant.PlanVm"/> oder null (gemischt bzw. nichts).</param>
public sealed record LicensingCluster(string Key, string Name, string Source, int Hosts, int WindowsVms, bool Mobile, MobilitySource Reason, bool? Setting, string? Licensing);

/// <param name="ClusterMobility">Je Cluster eingestellt, ob die VMs wandern (fehlt einer: automatisch).</param>
public sealed record LicensingSettings(IReadOnlyDictionary<string, decimal> Prices, bool ClusterWide)
{
    public IReadOnlyDictionary<string, bool> ClusterMobility { get; init; } = new Dictionary<string, bool>();
}

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
        var mobility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (await settings.GetAsync(SettingKeys.LicensingClusterMobility, ct) is { Length: > 0 } mobilityJson)
        {
            try
            {
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, bool>>(mobilityJson) ?? [])
                    mobility[key] = value;
            }
            catch (JsonException)
            {
                // Unlesbar: dann gilt für alle Cluster die Erkennung bzw. Voreinstellung.
            }
        }
        return new LicensingSettings(prices, await settings.GetAsync(SettingKeys.LicensingClusterWide, ct) != bool.FalseString) { ClusterMobility = mobility };
    }

    /// <summary>Stellt für einen Cluster ein, ob seine VMs zwischen den Hosts wandern; null = wieder automatisch.</summary>
    public async Task SaveClusterMobilityAsync(string clusterKey, bool? mobile, CancellationToken ct = default)
    {
        var mobility = new Dictionary<string, bool>((await GetSettingsAsync(ct)).ClusterMobility, StringComparer.OrdinalIgnoreCase);
        if (mobile is { } value)
            mobility[clusterKey] = value;
        else
            mobility.Remove(clusterKey);
        await settings.SetAsync(SettingKeys.LicensingClusterMobility, mobility.Count == 0 ? null : JsonSerializer.Serialize(mobility), ct);
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
        var (hosts, gaps) = await HostsAsync(options.ClusterMobility, ct);
        var stock = entitlements.GroupBy(e => e.Product).ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity));
        var result = WindowsServerEngine.Calculate(hosts, new WindowsServerOptions(options.Prices, options.ClusterWide, softwareAssurance, stock), Rules);

        var sqlProducts = new[] { MicrosoftRules.SqlEnterprise, MicrosoftRules.SqlStandard };
        var sqlAssurance = entitlements.Any(e => sqlProducts.Contains(e.Product) && e.SoftwareAssuranceActive(today));
        var sql = SqlServerEngine.Calculate(await SqlMachinesAsync(ct), hosts.ToDictionary(h => h.Key), new SqlServerOptions(options.Prices, sqlAssurance, stock, options.ClusterWide), Rules);

        var balances = Rules.Products.Select(product =>
        {
            var stock = entitlements.Where(e => e.Product == product.Key).Sum(e => e.Quantity);
            var sa = entitlements.Any(e => e.Product == product.Key && e.SoftwareAssuranceActive(today));
            int? need = windowsServerProducts.Contains(product.Key) ? result.Need.GetValueOrDefault(product.Key)
                : sqlProducts.Contains(product.Key) ? sql.Need.GetValueOrDefault(product.Key) : null;
            var price = Rules.Price(product.Key, options.Prices);
            return new ProductBalance(product, need, stock, sa, price > 0 ? product.PerCore ? price / Rules.PackSize : price : null);
        }).ToList();
        foreach (var expired in entitlements.Where(e => e.SoftwareAssuranceUntil is { } until && until < today))
            gaps.Add(new("Software Assurance of {0} ({1}) ended on {2}.", Rules.Product(expired.Product)?.Name ?? expired.Product, expired.ContractNumber ?? "–",
                expired.SoftwareAssuranceUntil!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        // Cluster der Virtualisierung (nicht die Failover-Cluster physischer Server): wie gerechnet wird und was eingestellt ist.
        var clusters = result.Hosts.Where(h => h.Host.ClusterKey is not null && h.Host.Source != "physical").GroupBy(h => h.Host.ClusterKey!)
            .Select(g =>
            {
                var first = g.First();
                var plans = g.Where(h => h.Chosen is not null).Select(h => h.Chosen!.Plan).Distinct().ToList();
                return new LicensingCluster(g.Key, first.Host.Cluster!, first.Host.Source, g.Count(), g.Sum(h => h.VmsOnHost),
                    WindowsServerEngine.IsMobile(first.Host, options.ClusterWide), first.Host.MobilitySource,
                    options.ClusterMobility.TryGetValue(g.Key, out var set) ? set : null, plans.Count == 1 ? plans[0] : null);
            })
            .OrderBy(c => c.Source, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new LicensingOverview(result, sql, balances, [.. gaps, .. result.Gaps, .. sql.Gaps], Rules, softwareAssurance, sqlAssurance, options.Prices,
            time.GetUtcNow().UtcDateTime) { Clusters = clusters };
    }

    /// <summary>
    /// Rechner mit SQL-Server-Instanzen. Bei VMs zählen die virtuellen Kerne laut Hypervisor (sonst laut Gast) und der Schlüssel des
    /// Hosts, wie <see cref="HostsAsync"/> ihn vergibt.
    /// </summary>
    private async Task<List<SqlMachine>> SqlMachinesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.AsNoTracking().Include(c => c.SqlInstances).Where(c => c.SqlInstances.Any()).ToListAsync(ct);
        var ids = computers.Select(c => c.Id).ToList();
        var hyperV = await db.HostedVirtualMachines.AsNoTracking().Where(v => v.GuestComputerId != null && ids.Contains(v.GuestComputerId.Value)).ToListAsync(ct);
        var managed = await db.HypervisorVms.AsNoTracking().Where(v => v.GuestComputerId != null && ids.Contains(v.GuestComputerId.Value)).ToListAsync(ct);
        return computers.Select(c =>
        {
            var vcpus = managed.FirstOrDefault(v => v.GuestComputerId == c.Id)?.VirtualCpus ?? hyperV.FirstOrDefault(v => v.GuestComputerId == c.Id)?.VirtualCpus
                        ?? c.CpuLogicalProcessors ?? c.CpuCores;
            var hostKey = c.HypervisorHostId is { } hypervisor ? $"hypervisor:{hypervisor}" : c.HostComputerId is { } host ? $"computer:{host}" : null;
            return new SqlMachine(c.Id, c.Name, c.IsVirtual, hostKey, c.CpuSockets, c.CpuCoresPerSocket, c.IsVirtual ? vcpus : null,
                c.SqlInstances.Select(i => new SqlInstanceInfo(i.Name, SqlServerEngine.ParseEdition(i.Edition), SqlServerEngine.MajorVersion(i.Version), i.Clustered)).ToList());
        }).ToList();
    }

    /// <summary>
    /// Die physischen Server: Hyper-V- und ESXi-Hosts mit ihren VMs, dazu Windows Server ohne Virtualisierung. VMs ohne bekannten Host
    /// werden als Lücke gemeldet.
    /// </summary>
    /// <param name="mobility">Je Cluster eingestellt, ob die VMs wandern; sonst gilt, was die Verwaltung meldet (DRS, HA), sonst die Voreinstellung.</param>
    private async Task<(List<LicensingHost> Hosts, List<LicensingNote> Gaps)> HostsAsync(IReadOnlyDictionary<string, bool> mobility, CancellationToken ct)
    {
        LicensingHost Mobility(LicensingHost host, bool? drs = null, bool? ha = null)
        {
            if (host.ClusterKey is not { } key)
                return host;
            if (mobility.TryGetValue(key, out var set))
                return host with { Mobile = set, MobilitySource = MobilitySource.Setting };
            // Eingeschaltetes DRS oder HA heißt sicher: VMs wandern. Ausgeschaltet heißt nur, dass sie nicht selbsttätig wandern – von Hand
            // geht es trotzdem; dann gilt die Voreinstellung.
            return drs == true ? host with { Mobile = true, MobilitySource = MobilitySource.Drs }
                : ha == true ? host with { Mobile = true, MobilitySource = MobilitySource.Ha }
                : host;
        }

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
                hosts.Add(Mobility(new LicensingHost($"computer:{hostId}", host.Name, host.ClusterName, "Hyper-V", host.CpuSockets, host.CpuCoresPerSocket, host.CpuCores,
                    true, host.OsEdition, hostId, vms) { ClusterKey = host.ClusterName is { } cluster ? $"hyperv|{cluster.ToUpperInvariant()}" : null }));
            }
            else
                // Die Verwaltung gehört zur Quelle: gleichnamige Cluster zweier vCenter sind verschiedene Cluster.
                hosts.Add(Mobility(new LicensingHost($"hypervisor:{view.Id}", view.Name, view.Cluster,
                    (view.Source switch { VirtualHostSource.XcpNg => "XCP-ng", VirtualHostSource.XenOrchestra => "Xen Orchestra", _ => "vCenter" }) + (view.ManagerName is { } manager ? $" · {manager}" : ""),
                    view.CpuSockets, null, view.CpuCores, false, null, null, vms)
                    { ClusterKey = view.Cluster is { } cluster ? $"m{view.ManagerId}|{cluster.ToUpperInvariant()}" : null }, view.ClusterDrs, view.ClusterHa));
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
                true, computer.OsEdition, computer.Id, [])
                { ClusterKey = computer.ClusterName is { } cluster ? $"physical|{cluster.ToUpperInvariant()}" : null });
        }
        return (hosts, gaps);
    }

    private static bool IsWindowsServer(Computer computer) =>
        computer.OsProductType is 2 or 3 || (computer.OsName ?? "").Contains("Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bedarf, Bestand und Differenz als Tabelle für CSV, Excel und PDF.</summary>
    /// <param name="total">Mit Summenzeile (für PDF und Oberfläche); die CSV-Ausgabe bleibt ohne, damit sie sich weiterverarbeiten lässt.</param>
    public ReportTable BalanceTable(LicensingOverview overview, bool total = false)
    {
        // Berechnete Produkte auch mit Bedarf 0: "nichts nötig" ist eine Aussage; nicht berechnete nur mit Bestand.
        var balances = overview.Balances.Where(b => b.Need is not null || b.Stock > 0).ToList();
        var rows = balances.Select(b => new string?[]
        {
            L[b.Product.Name], L[b.Product.PerCore ? "Cores" : "Licences"], b.Need?.ToString(CultureInfo.InvariantCulture), b.Stock.ToString(CultureInfo.InvariantCulture),
            b.Delta?.ToString(CultureInfo.InvariantCulture), Status(b), Money(b.NeedCost), Money(b.ShortfallCost),
        }).ToList();
        if (total)
            rows.Add([L["Total"], null, null, null, null, null, Money(balances.Sum(b => b.NeedCost ?? 0)), Money(balances.Sum(b => b.ShortfallCost ?? 0))]);
        return new(
        [
            new("product", L["Product"]), new("unit", L["Unit"]), new("need", L["Need"], true), new("stock", L["Stock"], true), new("delta", L["Difference"], true),
            new("status", L["Status"]), new("needCost", L["Cost of the need"], true), new("shortfallCost", L["Still to buy"], true),
        ], rows);
    }

    private static string? Money(decimal? value) => value?.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Je Host die gewählte Variante, mit den Kernen und den berücksichtigten VMs; Windows Server und SQL Server zusammen.</summary>
    public ReportTable HostTable(LicensingOverview overview) => HostTable(overview, overview.WindowsServer.Hosts.Concat(overview.SqlServer.Entries), total: false);

    /// <param name="total">Mit Summenzeile (Kerne und Kosten).</param>
    public ReportTable HostTable(LicensingOverview overview, IEnumerable<HostLicensing> entries, bool total)
    {
        // Auch Einträge ohne zulässige Variante (z. B. unbekannte Kerne) gehören in den Bericht; nur Hosts ganz ohne Last fehlen.
        var shown = entries.Where(Relevant).ToList();
        var chosen = shown.Where(h => h.Chosen is not null).ToList();
        var rows = shown.Select(h => new string?[]
        {
            h.Host.Name, h.Host.Cluster, SourceName(h.Host.Source), h.VmsOnHost.ToString(CultureInfo.InvariantCulture), h.WindowsVms.ToString(CultureInfo.InvariantCulture),
            h.Chosen is { } pick ? string.Join(" + ", pick.Parts.Select(p => L[overview.Rules.Product(p.Product)?.Name ?? p.Product] + (pick.Parts.Count > 1 ? $" ({p.Cores})" : "")))
                : L["not calculable"],
            h.Chosen?.Cores.ToString(CultureInfo.InvariantCulture), Money(h.Chosen?.Cost), Money(h.AdditionalCost),
        }).ToList();
        // Jede VM einmal: die auf dem Host laufenden zählen, nicht die im Cluster mitlizenzierten.
        if (total)
            rows.Add([L["Total"], null, null, shown.Sum(h => h.VmsOnHost).ToString(CultureInfo.InvariantCulture), null, null,
                chosen.Sum(h => h.Chosen!.Cores).ToString(CultureInfo.InvariantCulture), Money(chosen.Sum(h => h.Chosen!.Cost)), Money(chosen.Sum(h => h.AdditionalCost ?? 0))]);
        return new(
        [
            new("host", L["Host"]), new("cluster", L["Cluster"]), new("source", L["Source"]), new("vms", L["VMs on the host"], true), new("covered", L["VMs covered"], true),
            new("product", L["Licensed with"]), new("cores", L["Cores to license"], true), new("cost", L["Cost"], true), new("additional", L["Still to buy"], true),
        ], rows);
    }

    /// <summary>
    /// Der ausführliche Bericht: Zusammenfassung mit Summen, Windows Server und SQL Server je Host mit Rechenweg, Datenlücken,
    /// Bestand und die Regeln mit den Schwellen.
    /// </summary>
    public IReadOnlyList<ReportSection> ReportSections(LicensingOverview overview, IReadOnlyList<LicenseEntitlement> stock)
    {
        string Text(LicensingNote note) => L[note.Text, note.Args];
        var sections = new List<ReportSection>();
        var ws = overview.WindowsServer.Hosts.Where(Relevant).ToList();
        var sql = overview.SqlServer.Entries.Where(Relevant).ToList();
        var shortfall = overview.Balances.Sum(b => b.ShortfallCost ?? 0);
        sections.Add(new(L["Summary"],
        [
            L["Windows Server: {0} hosts, {1} cores to license. SQL Server: {2} servers or hosts, {3} cores to license.", ws.Count, ws.Sum(h => h.Chosen?.Cores ?? 0),
                sql.Count, sql.Sum(h => h.Chosen?.Cores ?? 0)],
            shortfall > 0 ? L["Still to buy at the prices used: {0}.", Money(shortfall)!] : L["The recorded stock covers the calculated need."],
            L["Software Assurance – Windows Server: {0}, SQL Server: {1}. Data gaps: {2}.", overview.SoftwareAssurance ? L["yes"] : L["no"], overview.SqlSoftwareAssurance ? L["yes"] : L["no"], overview.Gaps.Count],
        ], BalanceTable(overview, total: true), TotalRow: true));

        sections.Add(new("Windows Server", [L["Per physical host: the cheapest allowed variant after using the existing stock."]],
            HostTable(overview, overview.WindowsServer.Hosts, total: true), TotalRow: true, NewPage: true));
        if (overview.Clusters.Count > 0)
            sections.Add(new(L["Clusters"],
                [L["If VMs move between the hosts, every host is licensed for all VMs of its cluster (core licences may move only every {0} days); otherwise each host counts only its own VMs.", overview.Rules.WindowsServer.LicenceReassignmentDays]],
                new ReportTable(
                    [new("cluster", L["Cluster"]), new("source", L["Source"]), new("hosts", L["Hosts"], true), new("vms", L["Windows Server VMs"], true), new("mobile", L["VMs move"]), new("licensed", L["Licensed"])],
                    overview.Clusters.Select(c => new string?[]
                    {
                        c.Name, c.Source, c.Hosts.ToString(CultureInfo.InvariantCulture), c.WindowsVms.ToString(CultureInfo.InvariantCulture), MobilityText(c),
                        c.Licensing switch { LicenseVariant.PlanVm => L["per VM"], LicenseVariant.PlanHost => L["by host cores"], _ => L["per host"] },
                    }).ToList()), Level: 2));
        sections.Add(new("SQL Server", [L["Physical SQL servers per core; SQL VMs per VM or by licensing the whole host."]],
            HostTable(overview, overview.SqlServer.Entries, total: true), TotalRow: true));
        if (overview.SqlServer.Free.Count > 0)
            sections.Add(new(L["Free editions"], overview.SqlServer.Free.Select(n => "• " + Text(n)).ToList(), Level: 2));

        var first = true;
        foreach (var entry in ws.Select(h => ("Windows Server", h)).Concat(sql.Select(h => ("SQL Server", h))))
        {
            var (product, host) = entry;
            var paragraphs = host.Steps.Select((step, n) => $"{n + 1}. {Text(step)}").ToList();
            paragraphs.AddRange(host.Notes.Select(n => "• " + Text(n)));
            var variants = new ReportTable(
                [new("variant", L["Variant"]), new("cores", L["Cores"], true), new("cost", L["Cost"], true), new("note", L["Note"])],
                host.Variants.Select(v => new string?[]
                {
                    string.Join(" + ", v.Parts.Select(p => L[overview.Rules.Product(p.Product)?.Name ?? p.Product] + (v.Parts.Count > 1 ? $" ({p.Cores})" : ""))),
                    v.Cores.ToString(CultureInfo.InvariantCulture), Money(v.Cost),
                    v == host.Chosen ? L["chosen"] : v.Reason is { } reason ? Text(reason) : null,
                }).ToList());
            sections.Add(new($"{product}: {host.Host.Name}" + (host.Host.Cluster is { } cluster ? $" ({cluster})" : ""), paragraphs, variants, Level: 2,
                NewPage: first));
            first = false;
        }
        if (first)
            sections.Add(new(L["How it is calculated"], [L["No servers to calculate."]], NewPage: true));

        sections.Add(new(L["Data gaps"], overview.Gaps.Count == 0
            ? [L["No gaps: every server and virtual machine could be placed."]]
            : overview.Gaps.Select(g => "• " + Text(g)).ToList(), NewPage: true));
        sections.Add(new(L["Stock"], stock.Count == 0 ? [L["No licences recorded yet."]] : null,
            stock.Count == 0 ? null : new ReportTable(
                [new("product", L["Product"]), new("quantity", L["Quantity"], true), new("version", L["Version"]), new("sa", L["SA until"]), new("contract", L["Contract"])],
                stock.Select(e => new string?[]
                {
                    L[overview.Rules.Product(e.Product)?.Name ?? e.Product], e.Quantity.ToString(CultureInfo.InvariantCulture), e.Version,
                    e.SoftwareAssuranceUntil?.ToString("d", CultureInfo.CurrentCulture), e.ContractNumber,
                }).ToList())));
        var rules = overview.Rules;
        var breakEven = WindowsServerEngine.BreakEvenVms(rules.Price(MicrosoftRules.WindowsServerStandard, overview.Prices),
            rules.Price(MicrosoftRules.WindowsServerDatacenter, overview.Prices), rules.WindowsServer.StandardVmsPerLicense);
        sections.Add(new(L["Rules and assumptions"],
        [
            L["Windows Server: physical cores only, at least {0} per processor and {1} per server, packs of {2}. Standard covers {3} VMs per full licensing, Datacenter any number; per VM (at least {4} cores) only with SA. Where VMs move within a cluster, every host is licensed for all Windows Server VMs of the cluster, or all of them per VM.",
                rules.WindowsServer.MinCoresPerProcessor, rules.WindowsServer.MinCoresPerServer, rules.PackSize, rules.WindowsServer.StandardVmsPerLicense, rules.WindowsServer.PerVmMinCores],
            breakEven is { } vms ? L["At the prices used Datacenter pays off from {0} Windows Server VMs per host.", vms] : "",
            L["SQL Server: physical servers all cores (at least {0} per processor); VMs per VM (at least {1} cores; from SQL Server 2022 only with SA) or the whole host in the VMs' edition (as many VMs as core licences, with Enterprise and SA any number). Developer, Express and Evaluation are free.",
                rules.SqlServer.MinCoresPerProcessor, rules.SqlServer.MinCoresPerVm],
            L["Rules: Windows Server {0} (valid from {1}), SQL Server {2} (valid from {3}). {4}", rules.Version, rules.ValidFrom.ToString("d", CultureInfo.CurrentCulture),
                rules.SqlServer.Version, rules.SqlServer.ValidFrom.ToString("d", CultureInfo.CurrentCulture), rules.Source],
            L["Amounts are licence prices (your prices or the list prices as a reference) without Software Assurance or subscription fees."],
            L["An estimate from the inventory data according to the rules below, not a binding statement for an audit. Check the result against your contracts and the current Microsoft Product Terms."],
        ]));
        sections.Add(new(L["Microsoft documents"], [.. rules.Documents.Concat(rules.SqlServer.Documents).Select(d => $"• {d.Title}: {d.Url}")], Level: 2));
        return sections;
    }

    /// <summary>Ein Eintrag mit Last: gewählte Variante, Varianten, Rechenweg oder VMs; nicht ein Host ohne Windows-Server-Last.</summary>
    /// <summary>Ob die VMs eines Clusters wandern, und warum.</summary>
    public string MobilityText(LicensingCluster cluster) => (cluster.Mobile, cluster.Reason) switch
    {
        (true, MobilitySource.Drs) => L["yes (DRS is on)"],
        (true, MobilitySource.Ha) => L["yes (HA is on)"],
        (true, MobilitySource.Setting) => L["yes (set)"],
        (false, MobilitySource.Setting) => L["no (set)"],
        (true, _) => L["yes (default)"],
        _ => L["no (default)"],
    };

    private static bool Relevant(HostLicensing h) => h.Chosen is not null || h.Variants.Count > 0 || h.Steps.Count > 0 || h.WindowsVms > 0;

    public string SourceName(string source) => source switch
    {
        "physical" => L["physical server"],
        "VM" => L["VM without known host"],
        _ => source,
    };

    public string Status(ProductBalance balance) => balance.Delta switch
    {
        null => L["not calculated"],
        < 0 => L["shortfall"],
        0 => L["covered"],
        _ => L["surplus"],
    };
}
