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
    bool RunsWindowsServer, string? Edition, int? ComputerId, IReadOnlyList<LicensingVm> VirtualMachines)
{
    /// <summary>Eindeutiger Schlüssel des Clusters über alle Quellen (gleichnamige Cluster zweier vCenter sind verschiedene); null ohne Cluster.</summary>
    public string? ClusterKey { get => field ?? (Cluster is null ? null : $"{Source}|{Cluster.ToUpperInvariant()}"); init; }

    /// <summary>
    /// VMs wandern zwischen den Hosts des Clusters (Live-Migration, DRS, HA): true; sie bleiben auf ihrem Host und ziehen nur selten von Hand um:
    /// false; null = die Voreinstellung der Berechnung.
    /// </summary>
    public bool? Mobile { get; init; }

    /// <summary>Woher <see cref="Mobile"/> stammt; für die Erklärung.</summary>
    public MobilitySource MobilitySource { get; init; }
}

/// <summary>Woher die Angabe stammt, ob die VMs eines Clusters zwischen den Hosts wandern.</summary>
public enum MobilitySource
{
    /// <summary>Voreinstellung der Berechnung (nichts erkannt, nichts eingestellt).</summary>
    Default,
    /// <summary>Für diesen Cluster eingestellt.</summary>
    Setting,
    /// <summary>vSphere DRS ist eingeschaltet: VMs wandern selbsttätig.</summary>
    Drs,
    /// <summary>HA ist eingeschaltet: VMs starten nach einem Hostausfall auf einem anderen Host.</summary>
    Ha,
}

/// <param name="PricePerPack">Eigener Preis je 2-Kern-Paket nach Produktschlüssel; fehlt einer, gilt der Listenpreis der Regeldatei.</param>
/// <param name="ClusterWide">
/// Voreinstellung für Cluster ohne eigene Angabe (<see cref="LicensingHost.Mobile"/>): VMs wandern, also wird jeder Host für alle Windows-VMs
/// des Clusters lizenziert (Lizenzen nach Kernen dürfen nur alle 90 Tage umziehen, die VMs aber jederzeit zwischen lizenzierten Hosts).
/// </param>
/// <param name="SoftwareAssurance">Für Windows Server besteht Software Assurance oder ein Abonnement: Lizenzierung je VM ist erlaubt.</param>
/// <param name="Stock">Vorhandene Kerne je Produkt: was schon gekauft ist, kostet bei der Wahl der Variante nichts mehr.</param>
public sealed record WindowsServerOptions(IReadOnlyDictionary<string, decimal> PricePerPack, bool ClusterWide = true, bool SoftwareAssurance = false,
    IReadOnlyDictionary<string, int>? Stock = null);

/// <summary>Ein Teil einer Variante in einem Produkt, z. B. die Enterprise-VMs einer Lizenzierung je VM.</summary>
public sealed record LicensePart(string Product, int Cores, decimal Cost);

/// <param name="Product">Das Produkt; bei mehreren Teilen das höchste.</param>
/// <param name="Cores">Zu lizenzierende Kerne, auf Pakete gerundet (alle Teile zusammen).</param>
/// <param name="Reason">Warum die Variante nicht erlaubt ist.</param>
public sealed record LicenseVariant(string Product, int Cores, decimal Cost, bool Allowed, LicensingNote? Reason = null)
{
    /// <summary>Die Teile je Produkt (z. B. VMs in Enterprise und in Standard); ohne Angabe ein Teil aus Produkt, Kernen und Kosten.</summary>
    public IReadOnlyList<LicensePart> Parts { get => field is { Count: > 0 } ? field : [new LicensePart(Product, Cores, Cost)]; init; }

    /// <summary>
    /// Art der Lizenzierung im Cluster: <see cref="PlanHost"/> (alle Kerne jedes Hosts) oder <see cref="PlanVm"/> (jede VM für sich, die Lizenz
    /// wandert mit). In einem Cluster, dessen VMs wandern, nutzen alle Hosts dieselbe Art; gemischt wären umziehende VMs nicht gedeckt.
    /// </summary>
    public string? Plan { get; init; }

    public const string PlanHost = "host";
    public const string PlanVm = "vm";
}

/// <param name="LicensedCores">Kerne einer vollständigen Lizenzierung des Hosts (mit Mindestmengen).</param>
/// <param name="WindowsVms">Die VMs, für die der Host lizenziert wird (im Cluster ggf. alle des Clusters).</param>
/// <param name="Steps">Der Rechenweg mit den eingesetzten Zahlen, Schritt für Schritt.</param>
public sealed record HostLicensing(LicensingHost Host, int LicensedCores, int WindowsVms, IReadOnlyList<LicenseVariant> Variants, LicenseVariant? Chosen,
    IReadOnlyList<LicensingNote> Notes, IReadOnlyList<LicensingNote>? Steps = null)
{
    public IReadOnlyList<LicensingNote> Steps { get; init; } = Steps ?? [];

    /// <summary>Die VMs, die gerade auf dem Host laufen (ohne die übrigen des Clusters); für Summen, die jede VM einmal zählen.</summary>
    public int VmsOnHost { get; init; }

    /// <summary>Die VMs des Clusters dürfen auf diesen Host wandern.</summary>
    public bool Mobile { get; init; }

    /// <summary>Kerne der gewählten Variante, die der vorhandene Bestand deckt.</summary>
    public int FromStock { get; init; }

    /// <summary>Was für die gewählte Variante noch zu kaufen ist (anteilig, wenn der Bestand einen Teil deckt); null ohne Wahl.</summary>
    public decimal? AdditionalCost { get; init; }
}

public sealed record WindowsServerResult(IReadOnlyList<HostLicensing> Hosts, IReadOnlyDictionary<string, int> Need, IReadOnlyList<LicensingNote> Gaps, string RulesVersion);


/// <summary>
/// Berechnet den Bedarf an Windows-Server-Lizenzen je physischem Host: Kerne mit den Mindestmengen (je Prozessor, je Server),
/// dann die zulässigen Varianten Standard (je zwei VMs alle Kerne erneut), Datacenter (beliebig viele VMs) und je VM (nur mit
/// SA/Abonnement). Gewählt wird die günstigste; die übrigen bleiben sichtbar. Was fehlt, wird als Lücke gemeldet, nie geschätzt.
/// Wandern die VMs eines Clusters zwischen den Hosts (Live-Migration, DRS, HA), muss jeder Host für alle VMs lizenziert sein, die auf
/// ihm laufen können: Lizenzen nach Kernen dürfen nur alle 90 Tage umziehen, die VMs aber jederzeit. Je VM lizenzierte VMs nehmen ihre
/// Lizenz mit. Ein solcher Cluster wird deshalb als Ganzes auf eine Art lizenziert. Bleiben die VMs auf ihrem Host, zählt jeder Host nur
/// seine eigenen.
/// </summary>
public static class WindowsServerEngine
{
    public static WindowsServerResult Calculate(IReadOnlyList<LicensingHost> hosts, WindowsServerOptions options, MicrosoftRules rules)
    {
        var server = rules.WindowsServer;
        var gaps = new List<LicensingNote>();
        var results = new List<HostLicensing>();

        static bool Counts(LicensingVm v) => v.Running && v.WindowsServer != false;
        var clusterVms = hosts.Where(h => h.ClusterKey is not null).GroupBy(h => h.ClusterKey!).ToDictionary(g => g.Key, g => g.Sum(h => h.VirtualMachines.Count(Counts)));
        decimal Price(string product) => rules.Price(product, options.PricePerPack);
        decimal Cost(string product, int licensed) => Price(product) * licensed / rules.PackSize;

        foreach (var host in hosts)
        {
            var notes = new List<LicensingNote>();
            var vms = host.VirtualMachines.Where(Counts).ToList();
            var windowsVms = vms.Count;
            var mobile = IsMobile(host, options.ClusterWide);
            var inCluster = host.ClusterKey is { } clusterKey ? clusterVms.GetValueOrDefault(clusterKey) : windowsVms;
            var licensedVms = mobile ? Math.Max(inCluster, windowsVms) : windowsVms;
            if (host.VirtualMachines.Any(v => v.Running && v.WindowsServer is null))
                notes.Add(new("{0} running virtual machines with unknown operating system are counted as Windows Server.", host.VirtualMachines.Count(v => v.Running && v.WindowsServer is null)));
            if (host.Cluster is not null && inCluster > 0)
                notes.AddRange(MobilityNotes(host, mobile, inCluster, windowsVms, server.LicenceReassignmentDays));
            HostLicensing Result(int cores, int covered, IReadOnlyList<LicenseVariant> variants, IReadOnlyList<LicensingNote>? steps = null) =>
                new(host, cores, covered, variants, null, notes, steps) { VmsOnHost = windowsVms, Mobile = mobile };

            // Keine Windows-Last: ein ESXi-Host, auf dem keine Windows-VM laufen kann, braucht keine Windows-Server-Lizenz.
            if (licensedVms == 0 && !host.RunsWindowsServer)
            {
                results.Add(Result(0, 0, []));
                continue;
            }
            if (host.Edition is { } essentials && essentials.Contains("Essentials", StringComparison.OrdinalIgnoreCase))
            {
                gaps.Add(new("{0}: the Essentials edition is licensed per server and is not calculated here.", host.Name));
                results.Add(Result(0, licensedVms, []));
                continue;
            }
            var perSocket = host.CoresPerSocket ?? (host.Sockets is > 0 && host.TotalCores is { } total ? (total + host.Sockets.Value - 1) / host.Sockets.Value : null);
            if (host.Sockets is not > 0 || perSocket is not > 0)
            {
                gaps.Add(new("{0}: the number of processors or cores is unknown; the host cannot be calculated.", host.Name));
                results.Add(Result(0, licensedVms, []));
                continue;
            }
            var steps = new List<LicensingNote>();
            var perProcessor = host.Sockets.Value * Math.Max(perSocket.Value, server.MinCoresPerProcessor);
            steps.Add(new("{0} processors × {1} physical cores (at least {2} per processor): {3} cores.", host.Sockets.Value, perSocket.Value, server.MinCoresPerProcessor, perProcessor));
            var cores = rules.RoundToPacks(Math.Max(perProcessor, server.MinCoresPerServer));
            if (cores != perProcessor)
                steps.Add(new("At least {0} cores per server, in packs of {1}: {2} cores.", server.MinCoresPerServer, rules.PackSize, cores));

            steps.Add(licensedVms == windowsVms
                ? new("Windows Server VMs to cover: {0}.", licensedVms)
                : new("Windows Server VMs to cover: {0} (this host runs {1}; the cluster has {0}).", licensedVms, windowsVms));
            var variants = new List<LicenseVariant>();
            // Standard: eine vollständige Lizenzierung deckt zwei VMs (bzw. das Betriebssystem auf dem Host); weitere zwei VMs je Wiederholung.
            var standardRuns = Math.Max(1, (licensedVms + server.StandardVmsPerLicense - 1) / server.StandardVmsPerLicense);
            var standardCores = cores * standardRuns;
            var installedDatacenter = host.RunsWindowsServer && (host.Edition ?? "").Contains("Datacenter", StringComparison.OrdinalIgnoreCase);
            variants.Add(new LicenseVariant(MicrosoftRules.WindowsServerStandard, standardCores, Cost(MicrosoftRules.WindowsServerStandard, standardCores), !installedDatacenter,
                installedDatacenter ? new("The Datacenter edition is installed on the host.") : null) { Plan = LicenseVariant.PlanHost });
            variants.Add(new LicenseVariant(MicrosoftRules.WindowsServerDatacenter, cores, Cost(MicrosoftRules.WindowsServerDatacenter, cores), true) { Plan = LicenseVariant.PlanHost });
            steps.Add(new("Standard: one full licensing ({0} cores) covers {1} VMs; {2} VMs need it {3} times: {4} cores, {5:N0}.",
                cores, server.StandardVmsPerLicense, licensedVms, standardRuns, standardCores, Cost(MicrosoftRules.WindowsServerStandard, standardCores)));
            steps.Add(new("Datacenter: {0} cores cover any number of VMs on this host: {1:N0}.", cores, Cost(MicrosoftRules.WindowsServerDatacenter, cores)));
            if (BreakEvenVms(Price(MicrosoftRules.WindowsServerStandard), Price(MicrosoftRules.WindowsServerDatacenter), server.StandardVmsPerLicense) is { } breakEven)
                steps.Add(new("At these prices Datacenter pays off from {0} Windows Server VMs per host; below that Standard is cheaper.", breakEven));
            // Je VM: nur die VMs dieses Hosts – die Lizenz wandert mit der VM, im Cluster deckt so jede VM sich selbst. Das Betriebssystem auf dem
            // Host (Hyper-V) deckt das nicht, es braucht Kernlizenzen.
            if (!host.RunsWindowsServer && (vms.Count > 0 || mobile))
            {
                var perVmCores = vms.Sum(v => VmCores(v, rules));
                var allowed = !server.PerVmRequiresSoftwareAssurance || options.SoftwareAssurance;
                variants.Add(new LicenseVariant(MicrosoftRules.WindowsServerPerVm, perVmCores, Cost(MicrosoftRules.WindowsServerPerVm, perVmCores), allowed,
                    allowed ? null : new("Only with Software Assurance or a subscription.")) { Plan = LicenseVariant.PlanVm });
                steps.Add(vms.Count == 0
                    ? new("Per VM (only with SA or subscription): no Windows Server VM runs on this host right now; the licences of the cluster's VMs move with them: 0 cores.")
                    : new("Per VM (only with SA or subscription): each VM its vCPUs, at least {0}: {1} = {2} cores, {3:N0}.", server.PerVmMinCores,
                        string.Join(" + ", vms.Select(v => $"{v.Name} {VmCores(v, rules)}")), perVmCores, Cost(MicrosoftRules.WindowsServerPerVm, perVmCores)));
            }
            results.Add(Result(cores, licensedVms, variants, steps));
        }

        results = Choose(results, options.Stock);
        return new WindowsServerResult(results, Need(results), gaps, rules.Version);
    }

    /// <summary>Kerne je Produkt über alle gewählten Varianten.</summary>
    internal static Dictionary<string, int> Need(IEnumerable<HostLicensing> entries) => entries.Where(e => e.Chosen is not null).SelectMany(e => e.Chosen!.Parts)
        .GroupBy(p => p.Product).ToDictionary(g => g.Key, g => g.Sum(p => p.Cores));

    private static int VmCores(LicensingVm vm, MicrosoftRules rules) =>
        rules.RoundToPacks(Math.Max(vm.VirtualCpus ?? rules.WindowsServer.PerVmMinCores, rules.WindowsServer.PerVmMinCores));

    /// <summary>Wandern die VMs des Clusters dieses Hosts? Ohne Cluster nie; sonst die Angabe des Clusters oder die Voreinstellung.</summary>
    public static bool IsMobile(LicensingHost host, bool clusterWideDefault) => host.ClusterKey is not null && (host.Mobile ?? clusterWideDefault);

    /// <summary>Warum der Host für die VMs des ganzen Clusters oder nur für seine eigenen lizenziert wird.</summary>
    private static IEnumerable<LicensingNote> MobilityNotes(LicensingHost host, bool mobile, int inCluster, int onHost, int reassignmentDays)
    {
        yield return (mobile, host.MobilitySource) switch
        {
            (true, MobilitySource.Drs) => new("vSphere DRS is on in cluster {0}: VMs move between the hosts on their own.", host.Cluster!),
            (true, MobilitySource.Ha) => new("HA is on in cluster {0}: after a host failure its VMs start on another host.", host.Cluster!),
            (true, MobilitySource.Setting) => new("Cluster {0} is set to: VMs move between the hosts.", host.Cluster!),
            (true, _) => new("VMs of cluster {0} are assumed to move between the hosts (default under \"Prices and rules\").", host.Cluster!),
            (false, MobilitySource.Setting) => new("Cluster {0} is set to: VMs stay on their host and are moved only by hand.", host.Cluster!),
            (false, _) => new("VMs of cluster {0} are assumed to stay on their host (default under \"Prices and rules\").", host.Cluster!),
        };
        yield return mobile
            ? new("Core licences may move to another host only every {0} days, VMs at any time: every host is licensed for all {1} Windows Server VMs of the cluster – or every VM per VM, whose licence moves with it.",
                reassignmentDays, inCluster)
            : new("This host is licensed only for its own {0} Windows Server VMs. A VM moved by hand needs licences on the new host; core licences may move only every {1} days.",
                onHost, reassignmentDays);
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
    /// Wählt die Varianten mit den geringsten zusätzlichen Kosten: vorhandener Bestand wird zuerst verbraucht. Ein Cluster, dessen VMs
    /// wandern, wird als Ganzes auf eine Art lizenziert (alle Hosts nach Kernen oder alle VMs je VM), denn gemischt wäre eine umziehende
    /// VM auf dem Zielhost nicht gedeckt. Hosts ohne Wahl und die größten zuerst, damit flexible Hosts ihnen den passenden Bestand nicht
    /// wegnehmen. Ohne Bestand ist das schlicht die günstigste zulässige Variante.
    /// </summary>
    internal static List<HostLicensing> Choose(List<HostLicensing> hosts, IReadOnlyDictionary<string, int>? stock)
    {
        var remaining = new Dictionary<string, int>(stock ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<HostLicensing, HostLicensing>(ReferenceEqualityComparer.Instance);
        var candidates = hosts.Where(h => h.Variants.Count > 0).ToList();
        var units = candidates.Where(h => h.Mobile && h.Host.ClusterKey is not null).GroupBy(h => h.Host.ClusterKey!).Select(g => g.ToList())
            .Concat(candidates.Where(h => !(h.Mobile && h.Host.ClusterKey is not null)).Select(h => new List<HostLicensing> { h }))
            .OrderBy(u => u.Min(h => h.Variants.Count(v => v.Allowed))).ThenByDescending(u => u.Sum(h => h.LicensedCores))
            .ThenBy(u => u[0].Host.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var unit in units)
        {
            var clustered = unit[0].Mobile;
            // Im wandernden Cluster nur Arten, die jeder Host erlaubt; sonst (oder wenn keine passt) frei je Host.
            List<string?> plans = clustered
                ? unit.SelectMany(h => h.Variants).Where(v => v.Allowed && v.Plan is not null).Select(v => v.Plan).Distinct()
                    .Where(plan => unit.All(h => h.Variants.Any(v => v.Allowed && v.Plan == plan))).ToList()
                : [];
            if (plans.Count == 0)
                plans = [null];
            Trial? best = null;
            foreach (var plan in plans)
            {
                var trial = new Trial(plan, new Dictionary<string, int>(remaining, StringComparer.OrdinalIgnoreCase));
                foreach (var host in unit.OrderByDescending(h => h.LicensedCores).ThenBy(h => h.Host.Name, StringComparer.OrdinalIgnoreCase))
                {
                    // Je Teil anteilig: was der Bestand des Produkts deckt, kostet nichts mehr.
                    decimal Additional(LicenseVariant v) => v.Parts.Sum(p => p.Cores == 0 ? 0 : p.Cost * Math.Max(0, p.Cores - trial.Stock.GetValueOrDefault(p.Product)) / p.Cores);
                    var pick = host.Variants.Where(v => v.Allowed && (plan is null || v.Plan == plan)).OrderBy(Additional).ThenBy(v => v.Cost).ThenBy(v => v.Cores).FirstOrDefault();
                    var (fromStock, additional) = (0, 0m);
                    if (pick is not null)
                    {
                        additional = Additional(pick);
                        trial.Additional += additional;
                        trial.Cost += pick.Cost;
                        foreach (var part in pick.Parts)
                        {
                            var used = Math.Min(part.Cores, trial.Stock.GetValueOrDefault(part.Product));
                            fromStock += used;
                            trial.Stock[part.Product] = trial.Stock.GetValueOrDefault(part.Product) - used;
                        }
                    }
                    trial.Picks.Add((host, pick, fromStock, additional));
                }
                if (best is null || trial.Additional < best.Additional || (trial.Additional == best.Additional && trial.Cost < best.Cost))
                    best = trial;
            }
            remaining = best!.Stock;
            foreach (var (host, pick, fromStock, additional) in best.Picks)
            {
                var notes = host.Notes.ToList();
                if (clustered && unit.Count > 1 && best.Plan is not null)
                    notes.Add(best.Plan == LicenseVariant.PlanVm
                        ? new("The whole cluster is licensed per VM, so that the licences move with the VMs.")
                        : new("The whole cluster is licensed by the cores of every host, since every VM may run on every host."));
                if (pick is { Cores: > 0 })
                {
                    if (fromStock >= pick.Cores)
                        notes.Add(new("Covered by the existing stock."));
                    else if (fromStock > 0)
                        notes.Add(new("{0} of {1} cores covered by the existing stock.", fromStock, pick.Cores));
                }
                chosen[host] = host with { Chosen = pick, Notes = notes, FromStock = fromStock, AdditionalCost = pick is null ? null : additional };
            }
        }
        return hosts.Select(h => chosen.GetValueOrDefault(h) ?? h).ToList();
    }

    private sealed class Trial(string? plan, Dictionary<string, int> stock)
    {
        public string? Plan { get; } = plan;
        public Dictionary<string, int> Stock { get; } = stock;
        public List<(HostLicensing Host, LicenseVariant? Pick, int FromStock, decimal Additional)> Picks { get; } = [];
        public decimal Additional { get; set; }
        public decimal Cost { get; set; }
    }
}
