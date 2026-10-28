namespace Inventory.Core.Licensing;

/// <summary>Lizenzrelevante Edition, höchste zuerst.</summary>
public enum SqlEdition
{
    Enterprise,
    Standard,
    /// <summary>Nur über SPLA (Hosting) lizenzierbar.</summary>
    Web,
    Developer,
    Express,
    Evaluation,
    Unknown,
}

/// <summary>Eine SQL-Server-Instanz, wie die Berechnung sie braucht.</summary>
/// <param name="MajorVersion">16 = SQL Server 2022, 15 = 2019, 14 = 2017 …</param>
public sealed record SqlInstanceInfo(string Name, SqlEdition Edition, int? MajorVersion, bool Clustered);

/// <summary>Ein Betriebssystem mit SQL Server: physischer Server oder VM.</summary>
/// <param name="HostKey">Bei einer VM der Schlüssel ihres Hosts (wie in <see cref="LicensingHost.Key"/>); null = Host unbekannt.</param>
/// <param name="VirtualCpus">Bei einer VM die virtuellen Kerne.</param>
public sealed record SqlMachine(int ComputerId, string Name, bool Virtual, string? HostKey, int? Sockets, int? CoresPerSocket, int? VirtualCpus,
    IReadOnlyList<SqlInstanceInfo> Instances)
{
    /// <summary>Die höchste kostenpflichtige Edition; ohne eine solche die höchste überhaupt.</summary>
    public SqlEdition Edition => Instances.Count == 0 ? SqlEdition.Unknown : Instances.Min(i => i.Edition);

    public int? MaxMajorVersion => Instances.Where(i => i.Edition is SqlEdition.Enterprise or SqlEdition.Standard).Max(i => i.MajorVersion);
}

/// <param name="SoftwareAssurance">Für SQL Server besteht SA oder ein Abonnement.</param>
/// <param name="ClusterWide">Voreinstellung für Cluster ohne eigene Angabe: die VMs wandern zwischen den Hosts.</param>
public sealed record SqlServerOptions(IReadOnlyDictionary<string, decimal> PricePerPack, bool SoftwareAssurance = false, IReadOnlyDictionary<string, int>? Stock = null,
    bool ClusterWide = true);

/// <param name="Free">Instanzen ohne Lizenzkosten (Developer, Express, Evaluation), mit Warnung bei nicht-produktiven Editionen.</param>
public sealed record SqlServerResult(IReadOnlyList<HostLicensing> Entries, IReadOnlyDictionary<string, int> Need, IReadOnlyList<LicensingNote> Free, IReadOnlyList<LicensingNote> Gaps);

/// <summary>
/// SQL Server je Kern. Physische Server: alle physischen Kerne (mindestens 4 je Prozessor). VMs: je VM ihre vCPUs (mindestens 4);
/// ab SQL Server 2022 nur mit SA/Abonnement. Alternativ der ganze Host mit Enterprise: mit SA beliebig viele VMs, ohne SA so viele
/// VMs wie Kernlizenzen. Developer, Express und Evaluation kosten nichts; Developer und Evaluation dürfen nicht produktiv laufen.
/// Wandern die VMs eines Clusters: mit SA ziehen die Lizenzen mit (License Mobility innerhalb der Serverfarm); ohne SA bleiben sie 90 Tage
/// auf ihrem Host, jeder Host braucht dann Lizenzen für alle SQL-VMs des Clusters.
/// </summary>
public static class SqlServerEngine
{
    public static SqlEdition ParseEdition(string? edition)
    {
        var text = edition ?? "";
        bool Has(string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        // "Enterprise Evaluation" ist eine Testversion, kein Enterprise.
        if (Has("Evaluation")) return SqlEdition.Evaluation;
        if (Has("Developer")) return SqlEdition.Developer;
        if (Has("Express")) return SqlEdition.Express;
        if (Has("Enterprise")) return SqlEdition.Enterprise;
        if (Has("Standard") || Has("Business Intelligence")) return SqlEdition.Standard;
        if (Has("Web")) return SqlEdition.Web;
        return SqlEdition.Unknown;
    }

    public static int? MajorVersion(string? version) => int.TryParse((version ?? "").Split('.')[0], out var major) ? major : null;

    public static string Product(SqlEdition edition) => edition == SqlEdition.Enterprise ? MicrosoftRules.SqlEnterprise : MicrosoftRules.SqlStandard;

    public static SqlServerResult Calculate(IReadOnlyList<SqlMachine> machines, IReadOnlyDictionary<string, LicensingHost> hosts, SqlServerOptions options, MicrosoftRules rules)
    {
        var sql = rules.SqlServer;
        var entries = new List<HostLicensing>();
        var free = new List<LicensingNote>();
        var gaps = new List<LicensingNote>();
        decimal Cost(string product, int cores) => rules.Price(product, options.PricePerPack) * cores / rules.PackSize;

        foreach (var machine in machines)
        {
            foreach (var instance in machine.Instances.Where(i => i.Edition is SqlEdition.Developer or SqlEdition.Express or SqlEdition.Evaluation))
                free.Add(instance.Edition is SqlEdition.Express
                    ? new("{0}: instance {1} is Express – free.", machine.Name, instance.Name)
                    : new("{0}: instance {1} is {2} – free, but only for development and tests, not for production use.", machine.Name, instance.Name, instance.Edition.ToString()));
            if (machine.Instances.Any(i => i.Edition == SqlEdition.Web))
                gaps.Add(new("{0}: the Web edition can only be licensed through SPLA (hosting) and is not calculated here.", machine.Name));
            if (machine.Instances.Any(i => i.Edition == SqlEdition.Unknown))
                gaps.Add(new("{0}: the edition of an instance is unknown.", machine.Name));
        }
        var paid = machines.Where(m => m.Edition is SqlEdition.Enterprise or SqlEdition.Standard).ToList();

        // Physische Server: alle Kerne der Edition.
        foreach (var machine in paid.Where(m => !m.Virtual))
        {
            var host = new LicensingHost($"computer:{machine.ComputerId}", machine.Name, null, "physical", machine.Sockets, machine.CoresPerSocket, null, true, null, machine.ComputerId, []);
            if (machine.Sockets is not > 0 || machine.CoresPerSocket is not > 0)
            {
                gaps.Add(new("{0}: the number of processors or cores is unknown; SQL Server cannot be calculated.", machine.Name));
                continue;
            }
            var steps = new List<LicensingNote>();
            var cores = rules.RoundToPacks(machine.Sockets.Value * Math.Max(machine.CoresPerSocket.Value, sql.MinCoresPerProcessor));
            steps.Add(new("Physical server: {0} processors × {1} physical cores (at least {2} per processor): {3} cores.", machine.Sockets.Value, machine.CoresPerSocket.Value,
                sql.MinCoresPerProcessor, cores));
            var product = Product(machine.Edition);
            var variants = new List<LicenseVariant>
            {
                new(product, cores, Cost(product, cores), true)
                    { Formula = new("{0} processors × {1} cores, at least {2} per processor", machine.Sockets.Value, machine.CoresPerSocket.Value, sql.MinCoresPerProcessor) },
            };
            steps.Add(new("SQL Server {0} per core: {1} cores, {2:N0}.", machine.Edition.ToString(), cores, Cost(product, cores)));
            if (machine.Edition == SqlEdition.Standard)
                steps.Add(new("Alternative Standard server + CAL: {0:N0} for the server plus {1:N0} per user or device that uses it – cheaper only with few users.",
                    rules.Price(MicrosoftRules.SqlStandardServer, options.PricePerPack), rules.Price(MicrosoftRules.SqlCal, options.PricePerPack)));
            entries.Add(new HostLicensing(host, cores, 1, variants, null, [], steps) { VmsOnHost = 1 });
        }

        // VMs: je Host die Summe je VM gegen den ganzen Host. Ohne bekannten Host bleibt nur die Lizenzierung je VM, jede für sich.
        var virtualMachines = paid.Where(m => m.Virtual).ToList();
        foreach (var vm in virtualMachines.Where(m => m.HostKey is null || !hosts.ContainsKey(m.HostKey)))
            entries.Add(PerVmOnly(vm));
        var placed = virtualMachines.Where(m => m.HostKey is not null && hosts.ContainsKey(m.HostKey)).ToList();
        bool Mobile(LicensingHost host) => WindowsServerEngine.IsMobile(host, options.ClusterWide);
        var clusterSql = placed.Where(m => Mobile(hosts[m.HostKey!])).GroupBy(m => hosts[m.HostKey!].ClusterKey!).ToDictionary(g => g.Key, g => g.ToList());
        // Im wandernden Cluster alle Hosts, denn jede SQL-VM kann auf jeden von ihnen wandern.
        var hostKeys = placed.Select(m => m.HostKey!)
            .Concat(hosts.Values.Where(h => Mobile(h) && clusterSql.ContainsKey(h.ClusterKey!)).Select(h => h.Key))
            .Distinct().ToList();
        foreach (var key in hostKeys)
        {
            var hostInfo = hosts[key];
            var own = placed.Where(m => m.HostKey == key).ToList();
            var mobile = Mobile(hostInfo);
            var covered = mobile ? clusterSql[hostInfo.ClusterKey!] : own;
            // Mit SA wandert die Lizenz je VM mit der VM; ohne SA bleibt sie auf dem Host, und jeder Host braucht sie für alle VMs, die zu ihm wandern können.
            var perVmSet = options.SoftwareAssurance || !mobile ? own : covered;
            var steps = new List<LicensingNote>
            {
                covered.Count > own.Count
                    ? new("{0} SQL Server VMs on this host; {1} in cluster {2}, which may move here.", own.Count, covered.Count, hostInfo.Cluster!)
                    : new("{0} SQL Server VMs on this host.", own.Count),
            };
            var perVm = new List<string>();
            var perVmCost = 0m;
            var perVmCores = new Dictionary<string, int>();
            foreach (var vm in perVmSet)
            {
                var vmCores = rules.RoundToPacks(Math.Max(vm.VirtualCpus ?? sql.MinCoresPerVm, sql.MinCoresPerVm));
                var product = Product(vm.Edition);
                perVmCores[product] = perVmCores.GetValueOrDefault(product) + vmCores;
                perVmCost += Cost(product, vmCores);
                perVm.Add($"{vm.Name} ({vm.Edition}) {vmCores}");
            }
            var needsSa = perVmSet.Any(v => v.MaxMajorVersion is { } major && major >= sql.PerVmRequiresSoftwareAssuranceFromMajorVersion);
            var perVmAllowed = options.SoftwareAssurance || !needsSa;
            steps.Add(perVmSet.Count == 0
                ? new("Per VM: no SQL Server VM runs on this host right now; with SA the licences move with the VMs: 0 cores.")
                : new("Per VM: each VM its vCPUs, at least {0}: {1}; together {2:N0}.", sql.MinCoresPerVm, string.Join(" + ", perVm), perVmCost));
            if (mobile)
                steps.Add(options.SoftwareAssurance
                    ? new("With SA the licences move with the VMs within the server farm (License Mobility); each VM is licensed once.")
                    : new("Without SA licences stay on their host for {0} days: per VM, this host needs licences for all {1} SQL VMs of the cluster.", rules.WindowsServer.LicenceReassignmentDays, covered.Count));
            if (needsSa)
                steps.Add(new("SQL Server 2022 and later may be licensed per VM only with Software Assurance or a subscription."));

            var variants = new List<LicenseVariant>();
            // Eine Variante mit einem Teil je Edition: gewählt wird sie als Ganzes, gezählt und gegen den Bestand gerechnet je Produkt.
            var mainProduct = perVmCores.ContainsKey(MicrosoftRules.SqlEnterprise) || (perVmSet.Count == 0 && covered.Any(v => v.Edition == SqlEdition.Enterprise))
                ? MicrosoftRules.SqlEnterprise : MicrosoftRules.SqlStandard;
            variants.Add(new LicenseVariant(mainProduct, perVmCores.Values.Sum(), perVmCost, perVmAllowed, perVmAllowed ? null : new("Only with Software Assurance or a subscription."))
            {
                Plan = options.SoftwareAssurance ? LicenseVariant.PlanVm : LicenseVariant.PlanHost,
                Parts = perVmCores.Select(p => new LicensePart(p.Key, p.Value, Cost(p.Key, p.Value))).ToList(),
                Formula = perVmSet.Count == 0 ? new("no SQL Server VM on this host right now")
                    : perVmSet.Count > own.Count ? new("{0} VMs of the cluster × their vCPUs, at least {1} each (without SA on every host)", perVmSet.Count, sql.MinCoresPerVm)
                    : new("{0} VMs × their vCPUs, at least {1} each", perVmSet.Count, sql.MinCoresPerVm),
            });

            var perSocket = hostInfo.CoresPerSocket ?? (hostInfo.Sockets is > 0 && hostInfo.TotalCores is { } total ? (total + hostInfo.Sockets.Value - 1) / hostInfo.Sockets.Value : null);
            if (hostInfo.Sockets is > 0 && perSocket is > 0)
            {
                var hostCores = rules.RoundToPacks(hostInfo.Sockets.Value * Math.Max(perSocket.Value, sql.MinCoresPerProcessor));
                // Alle physischen Kerne in der höchsten Edition der VMs: so viele VMs wie Kernlizenzen. Mit SA und Enterprise beliebig viele.
                var hostEdition = covered.Min(v => v.Edition);
                var hostProduct = Product(hostEdition);
                var hostCost = Cost(hostProduct, hostCores);
                var unlimited = options.SoftwareAssurance && hostEdition == SqlEdition.Enterprise;
                var hostAllowed = unlimited || covered.Count <= hostCores;
                variants.Add(new LicenseVariant(hostProduct, hostCores, hostCost, hostAllowed,
                    hostAllowed ? null : new("Without SA the full licensing covers only as many VMs as core licences."))
                {
                    Plan = LicenseVariant.PlanHost,
                    Formula = unlimited ? new("all {0} cores of the host, with SA any number of SQL VMs", hostCores) : new("all {0} cores of the host, covers up to {0} SQL VMs", hostCores),
                });
                steps.Add(new("Whole host in {0}: {1} processors × {2} cores (at least {3} per processor) = {4} cores, {5:N0}; covers up to {4} SQL VMs.",
                    hostEdition.ToString(), hostInfo.Sockets.Value, perSocket.Value, sql.MinCoresPerProcessor, hostCores, hostCost));
                if (options.SoftwareAssurance && hostEdition == SqlEdition.Standard)
                {
                    var enterpriseCost = Cost(MicrosoftRules.SqlEnterprise, hostCores);
                    variants.Add(new LicenseVariant(MicrosoftRules.SqlEnterprise, hostCores, enterpriseCost, true)
                        { Plan = LicenseVariant.PlanHost, Formula = new("all {0} cores of the host, with SA any number of SQL VMs", hostCores) });
                    steps.Add(new("Whole host in Enterprise with SA: {0} cores, {1:N0}; any number of SQL VMs.", hostCores, enterpriseCost));
                }
                else if (unlimited)
                    steps.Add(new("With Enterprise and SA any number of SQL VMs may run on the fully licensed host."));
                var averageVm = perVmSet.Count == 0 ? 0 : perVmCost / perVmSet.Count;
                if (averageVm > 0)
                    steps.Add(new("The whole host pays off from about {0} SQL VMs of this size on it.", (int)Math.Ceiling(hostCost / averageVm)));
            }
            else
                steps.Add(new("Whole host: the number of processors or cores of the host is unknown."));
            if (!variants.Any(v => v.Allowed))
                gaps.Add(new("{0}: no allowed licensing for its SQL VMs – per VM needs SA, and without SA the host covers only as many VMs as core licences.", hostInfo.Name));
            entries.Add(new HostLicensing(hostInfo with { VirtualMachines = own.Select(v => new LicensingVm(v.Name, v.VirtualCpus, true, true, v.ComputerId)).ToList() },
                0, covered.Count, variants, null, [], steps) { VmsOnHost = own.Count, Mobile = mobile });
        }

        HostLicensing PerVmOnly(SqlMachine vm)
        {
            var cores = rules.RoundToPacks(Math.Max(vm.VirtualCpus ?? sql.MinCoresPerVm, sql.MinCoresPerVm));
            var product = Product(vm.Edition);
            var needsSa = vm.MaxMajorVersion is { } major && major >= sql.PerVmRequiresSoftwareAssuranceFromMajorVersion;
            var allowed = options.SoftwareAssurance || !needsSa;
            var steps = new List<LicensingNote>
            {
                new("Virtual machine without a known host: only licensing per VM is possible."),
                new("{0} vCPUs, at least {1}: {2} cores {3}, {4:N0}.", vm.VirtualCpus?.ToString() ?? "?", sql.MinCoresPerVm, cores, vm.Edition.ToString(), Cost(product, cores)),
            };
            if (!allowed)
            {
                steps.Add(new("SQL Server 2022 and later may be licensed per VM only with Software Assurance or a subscription."));
                gaps.Add(new("{0}: SQL Server 2022 or later on a VM without a known host and without SA cannot be licensed correctly.", vm.Name));
            }
            var host = new LicensingHost($"computer:{vm.ComputerId}", vm.Name, null, "VM", null, null, vm.VirtualCpus, false, null, vm.ComputerId,
                [new LicensingVm(vm.Name, vm.VirtualCpus, true, true, vm.ComputerId)]);
            return new HostLicensing(host, 0, 1, [new LicenseVariant(product, cores, Cost(product, cores), allowed, allowed ? null : new("Only with Software Assurance or a subscription."))
                { Formula = new("{0} vCPUs, at least {1}", vm.VirtualCpus?.ToString() ?? "?", sql.MinCoresPerVm) }], null, [], steps)
                { VmsOnHost = 1 };
        }

        entries = WindowsServerEngine.Choose(entries, options.Stock);
        return new SqlServerResult(entries, WindowsServerEngine.Need(entries), free, gaps);
    }
}
