namespace Inventory.Core.Licensing;

/// <summary>Ein Hinweis zur Berechnung; Text ist der englische Ausgangstext (zugleich Übersetzungsschlüssel), Args seine Platzhalter.</summary>
public sealed record LicensingNote(string Text, params object[] Args);

/// <param name="WindowsServer">true = Windows Server, false = sicher kein Windows Server, null = unbekannt (zählt vorsichtshalber mit).</param>
public sealed record LicensingVm(string Name, int? VirtualCpus, bool Running, bool? WindowsServer, int? ComputerId);

/// <summary>Ein physischer Server: Hyper-V-Host, ESXi-Host oder Windows Server ohne VMs.</summary>
/// <param name="Key">Eindeutig über alle Quellen, z. B. "vcenter:12" oder "computer:5".</param>
/// <param name="RunsWindowsServer">Auf dem Host selbst läuft Windows Server (Hyper-V oder ohne Virtualisierung); bei ESXi false.</param>
/// <param name="Edition">EditionID des Windows auf dem Host, z. B. ServerDatacenter.</param>
public sealed record LicensingHost(string Key, string Name, string? Cluster, string Source, int? Sockets, int? CoresPerSocket, int? TotalCores,
    bool RunsWindowsServer, string? Edition, int? ComputerId, IReadOnlyList<LicensingVm> VirtualMachines);

/// <param name="PricePerPack">Eigener Preis je 2-Kern-Paket nach Produktschlüssel; fehlt einer, gilt der Listenpreis der Regeldatei.</param>
/// <param name="ClusterWide">Jeder Host im Cluster wird für alle Windows-VMs des Clusters lizenziert (Lizenzen dürfen nur alle 90 Tage umziehen).</param>
/// <param name="SoftwareAssurance">Für Windows Server besteht Software Assurance oder ein Abonnement: Lizenzierung je VM ist erlaubt.</param>
/// <param name="Stock">Vorhandene Kerne je Produkt: was schon gekauft ist, kostet bei der Wahl der Variante nichts mehr.</param>
public sealed record WindowsServerOptions(IReadOnlyDictionary<string, decimal> PricePerPack, bool ClusterWide = true, bool SoftwareAssurance = false,
    IReadOnlyDictionary<string, int>? Stock = null);

/// <param name="Cores">Zu lizenzierende Kerne, auf Pakete gerundet.</param>
/// <param name="Reason">Warum die Variante nicht erlaubt ist.</param>
public sealed record LicenseVariant(string Product, int Cores, decimal Cost, bool Allowed, LicensingNote? Reason = null);

/// <param name="LicensedCores">Kerne einer vollständigen Lizenzierung des Hosts (mit Mindestmengen).</param>
/// <param name="WindowsVms">Die VMs, für die der Host lizenziert wird (im Cluster ggf. alle des Clusters).</param>
/// <param name="Steps">Der Rechenweg mit den eingesetzten Zahlen, Schritt für Schritt.</param>
public sealed record HostLicensing(LicensingHost Host, int LicensedCores, int WindowsVms, IReadOnlyList<LicenseVariant> Variants, LicenseVariant? Chosen,
    IReadOnlyList<LicensingNote> Notes, IReadOnlyList<LicensingNote>? Steps = null)
{
    public IReadOnlyList<LicensingNote> Steps { get; init; } = Steps ?? [];
}

public sealed record WindowsServerResult(IReadOnlyList<HostLicensing> Hosts, IReadOnlyDictionary<string, int> Need, IReadOnlyList<LicensingNote> Gaps, string RulesVersion);

/// <summary>
/// Berechnet den Bedarf an Windows-Server-Lizenzen je physischem Host: Kerne mit den Mindestmengen (je Prozessor, je Server),
/// dann die zulässigen Varianten Standard (je zwei VMs alle Kerne erneut), Datacenter (beliebig viele VMs) und je VM (nur mit
/// SA/Abonnement). Gewählt wird die günstigste; die übrigen bleiben sichtbar. Was fehlt, wird als Lücke gemeldet, nie geschätzt.
/// </summary>
public static class WindowsServerEngine
{
    public static WindowsServerResult Calculate(IReadOnlyList<LicensingHost> hosts, WindowsServerOptions options, MicrosoftRules rules)
    {
        var server = rules.WindowsServer;
        var gaps = new List<LicensingNote>();
        var results = new List<HostLicensing>();

        int Relevant(LicensingHost host) => host.VirtualMachines.Count(v => v.Running && v.WindowsServer != false);
        var clusterVms = hosts.Where(h => h.Cluster is not null)
            .GroupBy(h => (h.Source, Cluster: h.Cluster!.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Sum(Relevant));

        foreach (var host in hosts)
        {
            var notes = new List<LicensingNote>();
            var windowsVms = Relevant(host);
            if (host.VirtualMachines.Any(v => v.Running && v.WindowsServer is null))
                notes.Add(new("{0} running virtual machines with unknown operating system are counted as Windows Server.", host.VirtualMachines.Count(v => v.Running && v.WindowsServer is null)));
            // Keine Windows-Last: ein ESXi-Host ohne Windows-VMs braucht keine Windows-Server-Lizenz.
            if (windowsVms == 0 && !host.RunsWindowsServer)
            {
                results.Add(new HostLicensing(host, 0, 0, [], null, notes));
                continue;
            }
            if (host.Edition is { } essentials && essentials.Contains("Essentials", StringComparison.OrdinalIgnoreCase))
            {
                gaps.Add(new("{0}: the Essentials edition is licensed per server and is not calculated here.", host.Name));
                results.Add(new HostLicensing(host, 0, windowsVms, [], null, notes));
                continue;
            }
            var perSocket = host.CoresPerSocket ?? (host.Sockets is > 0 && host.TotalCores is { } total ? (total + host.Sockets.Value - 1) / host.Sockets.Value : null);
            if (host.Sockets is not > 0 || perSocket is not > 0)
            {
                gaps.Add(new("{0}: the number of processors or cores is unknown; the host cannot be calculated.", host.Name));
                results.Add(new HostLicensing(host, 0, windowsVms, [], null, notes));
                continue;
            }
            var steps = new List<LicensingNote>();
            var perProcessor = host.Sockets.Value * Math.Max(perSocket.Value, server.MinCoresPerProcessor);
            steps.Add(new("{0} processors × {1} physical cores (at least {2} per processor): {3} cores.", host.Sockets.Value, perSocket.Value, server.MinCoresPerProcessor, perProcessor));
            var cores = rules.RoundToPacks(Math.Max(perProcessor, server.MinCoresPerServer));
            if (cores != perProcessor)
                steps.Add(new("At least {0} cores per server, in packs of {1}: {2} cores.", server.MinCoresPerServer, rules.PackSize, cores));

            var licensedVms = windowsVms;
            if (options.ClusterWide && host.Cluster is not null && clusterVms.TryGetValue((host.Source, host.Cluster.ToUpperInvariant()), out var inCluster) && inCluster > windowsVms)
            {
                licensedVms = inCluster;
                notes.Add(new("Licensed for all {0} Windows Server VMs of cluster {1}, since any of them may run on this host.", inCluster, host.Cluster));
            }

            decimal Price(string product) => rules.Price(product, options.PricePerPack);
            decimal Cost(string product, int licensed) => Price(product) * licensed / rules.PackSize;

            steps.Add(licensedVms == windowsVms
                ? new("Windows Server VMs to cover: {0}.", licensedVms)
                : new("Windows Server VMs to cover: {0} (this host runs {1}; the cluster has {0}).", licensedVms, windowsVms));
            var variants = new List<LicenseVariant>();
            // Standard: eine vollständige Lizenzierung deckt zwei VMs (bzw. das Betriebssystem auf dem Host); weitere zwei VMs je Wiederholung.
            var standardRuns = Math.Max(1, (licensedVms + server.StandardVmsPerLicense - 1) / server.StandardVmsPerLicense);
            var standardCores = cores * standardRuns;
            var installedDatacenter = host.RunsWindowsServer && (host.Edition ?? "").Contains("Datacenter", StringComparison.OrdinalIgnoreCase);
            variants.Add(new LicenseVariant(MicrosoftRules.WindowsServerStandard, standardCores, Cost(MicrosoftRules.WindowsServerStandard, standardCores), !installedDatacenter,
                installedDatacenter ? new("The Datacenter edition is installed on the host.") : null));
            variants.Add(new LicenseVariant(MicrosoftRules.WindowsServerDatacenter, cores, Cost(MicrosoftRules.WindowsServerDatacenter, cores), true));
            steps.Add(new("Standard: one full licensing ({0} cores) covers {1} VMs; {2} VMs need it {3} times: {4} cores, {5:N0}.",
                cores, server.StandardVmsPerLicense, licensedVms, standardRuns, standardCores, Cost(MicrosoftRules.WindowsServerStandard, standardCores)));
            steps.Add(new("Datacenter: {0} cores cover any number of VMs on this host: {1:N0}.", cores, Cost(MicrosoftRules.WindowsServerDatacenter, cores)));
            if (BreakEvenVms(Price(MicrosoftRules.WindowsServerStandard), Price(MicrosoftRules.WindowsServerDatacenter), server.StandardVmsPerLicense) is { } breakEven)
                steps.Add(new("At these prices Datacenter pays off from {0} Windows Server VMs per host; below that Standard is cheaper.", breakEven));
            // Je VM: nur die VMs dieses Hosts (die Lizenz wandert mit der VM), Mindestmenge je VM; das Betriebssystem auf dem Host deckt das nicht.
            var vms = host.VirtualMachines.Where(v => v.Running && v.WindowsServer != false).ToList();
            if (vms.Count > 0 && !host.RunsWindowsServer)
            {
                var perVmCores = vms.Sum(v => rules.RoundToPacks(Math.Max(v.VirtualCpus ?? server.PerVmMinCores, server.PerVmMinCores)));
                var allowed = !server.PerVmRequiresSoftwareAssurance || options.SoftwareAssurance;
                variants.Add(new LicenseVariant(MicrosoftRules.WindowsServerPerVm, perVmCores, Cost(MicrosoftRules.WindowsServerPerVm, perVmCores), allowed,
                    allowed ? null : new("Only with Software Assurance or a subscription.")));
                steps.Add(new("Per VM (only with SA or subscription): each VM its vCPUs, at least {0}: {1} = {2} cores, {3:N0}.", server.PerVmMinCores,
                    string.Join(" + ", vms.Select(v => $"{v.Name} {rules.RoundToPacks(Math.Max(v.VirtualCpus ?? server.PerVmMinCores, server.PerVmMinCores))}")), perVmCores,
                    Cost(MicrosoftRules.WindowsServerPerVm, perVmCores)));
            }
            results.Add(new HostLicensing(host, cores, licensedVms, variants, null, notes, steps));
        }

        results = Choose(results, options.Stock);
        var need = results.Where(r => r.Chosen is not null).GroupBy(r => r.Chosen!.Product).ToDictionary(g => g.Key, g => g.Sum(r => r.Chosen!.Cores));
        return new WindowsServerResult(results, need, gaps, rules.Version);
    }

    /// <summary>
    /// Ab wie vielen VMs je Host Datacenter günstiger ist als Standard: Standard kostet je zwei VMs einmal alle Kerne, Datacenter
    /// einmal; beide je Kern, die Kernzahl kürzt sich heraus. null, wenn kein Preis für Standard bekannt ist.
    /// </summary>
    public static int? BreakEvenVms(decimal standardPerPack, decimal datacenterPerPack, int vmsPerStandardLicense)
    {
        if (standardPerPack <= 0)
            return null;
        var runs = (int)Math.Floor(datacenterPerPack / standardPerPack) + 1;
        return (runs - 1) * vmsPerStandardLicense + 1;
    }

    /// <summary>
    /// Wählt je Host die Variante mit den geringsten zusätzlichen Kosten: vorhandener Bestand wird zuerst verbraucht, Hosts ohne
    /// Wahl und die größten Hosts zuerst. Ohne Bestand ist das schlicht die günstigste zulässige Variante.
    /// </summary>
    internal static List<HostLicensing> Choose(List<HostLicensing> hosts, IReadOnlyDictionary<string, int>? stock)
    {
        var remaining = new Dictionary<string, int>(stock ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<HostLicensing, HostLicensing>();
        // Hosts ohne Wahl (z. B. Datacenter installiert) zuerst, damit flexible Hosts ihnen den passenden Bestand nicht wegnehmen.
        foreach (var host in hosts.Where(h => h.Variants.Count > 0)
                     .OrderBy(h => h.Variants.Count(v => v.Allowed)).ThenByDescending(h => h.LicensedCores).ThenBy(h => h.Host.Name, StringComparer.OrdinalIgnoreCase))
        {
            decimal Additional(LicenseVariant v) => v.Cost * Math.Max(0, v.Cores - remaining.GetValueOrDefault(v.Product)) / Math.Max(1, v.Cores);
            var pick = host.Variants.Where(v => v.Allowed).OrderBy(Additional).ThenBy(v => v.Cost).ThenBy(v => v.Cores).FirstOrDefault();
            var notes = host.Notes.ToList();
            if (pick is not null)
            {
                var available = remaining.GetValueOrDefault(pick.Product);
                if (available >= pick.Cores)
                    notes.Add(new("Covered by the existing stock."));
                else if (available > 0)
                    notes.Add(new("{0} of {1} cores covered by the existing stock.", available, pick.Cores));
                remaining[pick.Product] = Math.Max(0, available - pick.Cores);
            }
            chosen[host] = host with { Chosen = pick, Notes = notes };
        }
        return hosts.Select(h => chosen.GetValueOrDefault(h) ?? h).ToList();
    }
}
