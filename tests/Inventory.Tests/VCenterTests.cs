using System.Net;
using System.Text;
using System.Xml.Linq;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

/// <summary>vCenter-Anbindung, nur gegen nachgebaute SOAP-Antworten; nie gegen ein echtes vCenter.</summary>
public class VCenterTests
{
    private static readonly XNamespace Vim = "urn:vim25";

    /// <summary>Beantwortet die vim25-Aufrufe aus festen Daten und merkt sich, welche Methoden aufgerufen wurden.</summary>
    private sealed class FakeVCenter(bool rejectLogin = false) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).Descendants().First(e => e.Name.LocalName == "Body").Elements().First();
            var method = body.Name.LocalName;
            Calls.Add(method);
            string Return(string inner) => $"<{method}Response xmlns=\"urn:vim25\"><returnval>{inner}</returnval></{method}Response>";
            var answer = method switch
            {
                "RetrieveServiceContent" => Return("<rootFolder type=\"Folder\">group-d1</rootFolder><propertyCollector type=\"PropertyCollector\">propertyCollector</propertyCollector>"
                    + "<viewManager type=\"ViewManager\">ViewManager</viewManager><about><fullName>VMware vCenter Server 8.0.3 build-1</fullName></about>"
                    + "<sessionManager type=\"SessionManager\">SessionManager</sessionManager>"),
                "Login" when rejectLogin => null,
                "Login" => Return("<key>session</key>"),
                "Logout" => $"<LogoutResponse xmlns=\"urn:vim25\"/>",
                "CreateContainerView" => Return($"session[{body.Element(Vim + "type")!.Value}]"),
                "RetrievePropertiesEx" => Return(Page(body.Descendants(Vim + "type").First().Value, first: true)),
                "ContinueRetrievePropertiesEx" => Return(Page("VirtualMachine", first: false)),
                _ => throw new InvalidOperationException(method),
            };
            var envelope = answer is null
                ? "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body><soapenv:Fault><faultcode>ServerFaultCode</faultcode>"
                  + "<faultstring>Cannot complete login due to an incorrect user name or password.</faultstring><detail><InvalidLoginFault xmlns=\"urn:vim25\"/></detail></soapenv:Fault></soapenv:Body></soapenv:Envelope>"
                : $"<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><soapenv:Body>{answer}</soapenv:Body></soapenv:Envelope>";
            return new HttpResponseMessage(answer is null ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        }

        private static string Object(string type, string id, params (string Name, string Value)[] properties) =>
            $"<objects><obj type=\"{type}\">{id}</obj>" + string.Concat(properties.Select(p => $"<propSet><name>{p.Name}</name>{p.Value}</propSet>")) + "</objects>";

        private static string Val(string type, string value) => $"<val xsi:type=\"{type}\">{value}</val>";

        private static string Page(string type, bool first) => type switch
        {
            "ClusterComputeResource" => Object(type, "domain-c7", ("name", Val("xsd:string", "PROD"))),
            "HostSystem" => Object(type, "host-11", ("name", Val("xsd:string", "esx01.corp.example")), ("parent", "<val type=\"ClusterComputeResource\" xsi:type=\"ManagedObjectReference\">domain-c7</val>"),
                    ("summary.hardware.numCpuPkgs", Val("xsd:short", "2")), ("summary.hardware.numCpuCores", Val("xsd:short", "32")),
                    ("summary.hardware.model", Val("xsd:string", "PowerEdge R750")), ("runtime.connectionState", Val("HostSystemConnectionState", "connected")))
                + Object(type, "host-21", ("name", Val("xsd:string", "esx-solo")), ("parent", "<val type=\"ComputeResource\" xsi:type=\"ManagedObjectReference\">domain-s9</val>"),
                    ("summary.hardware.numCpuPkgs", Val("xsd:short", "1")), ("summary.hardware.numCpuCores", Val("xsd:short", "12")), ("runtime.connectionState", Val("HostSystemConnectionState", "disconnected"))),
            // Die VMs kommen auf zwei Seiten: die erste trägt ein Token für die Fortsetzung.
            "VirtualMachine" when first => Object(type, "vm-1", ("name", Val("xsd:string", "VM-SQL01")), ("config.uuid", Val("xsd:string", "421c4d6e-1234-5678-9abc-def012345678")),
                    ("config.hardware.numCPU", Val("xsd:int", "8")), ("config.template", Val("xsd:boolean", "false")), ("runtime.host", "<val type=\"HostSystem\" xsi:type=\"ManagedObjectReference\">host-11</val>"),
                    ("runtime.powerState", Val("VirtualMachinePowerState", "poweredOn")), ("guest.hostName", Val("xsd:string", "vm-sql01.corp.example")),
                    ("config.guestFullName", Val("xsd:string", "Microsoft Windows Server 2022 (64-bit)")))
                + Object(type, "vm-2", ("name", Val("xsd:string", "Template-2022")), ("config.template", Val("xsd:boolean", "true")))
                + "<token>next</token>",
            "VirtualMachine" => Object(type, "vm-3", ("name", Val("xsd:string", "VM-OFF")), ("config.hardware.numCPU", Val("xsd:int", "2")),
                ("runtime.host", "<val type=\"HostSystem\" xsi:type=\"ManagedObjectReference\">host-21</val>"), ("runtime.powerState", Val("VirtualMachinePowerState", "poweredOff"))),
            _ => "",
        };
    }

    private static HypervisorConnection Connection(string url = "https://vcenter.corp.example") => new(url, new NetworkCredential("reader@vsphere.local", "secret"), null);

    [Fact]
    public async Task Hosts_clusters_and_machines_are_read_over_all_pages_and_the_session_is_closed()
    {
        var fake = new FakeVCenter();
        var inventory = await new VCenterSource(_ => fake).ReadAsync(Connection());

        Assert.Equal("VMware vCenter Server 8.0.3 build-1", inventory.Version);
        Assert.Equal(
            [new HypervisorHostInfo("host-11", "esx01.corp.example", "PROD", 2, 32, "PowerEdge R750", true), new HypervisorHostInfo("host-21", "esx-solo", null, 1, 12, null, false)],
            inventory.Hosts);
        // Vorlagen zählen nicht; die zweite Seite wird nachgeladen.
        Assert.Equal(["VM-SQL01", "VM-OFF"], inventory.VirtualMachines.Select(v => v.Name));
        var sql = inventory.VirtualMachines[0];
        Assert.Equal(("421c4d6e-1234-5678-9abc-def012345678", 8, "host-11", true, "vm-sql01.corp.example"), (sql.BiosUuid, sql.VirtualCpus!.Value, sql.HostId, sql.PoweredOn, sql.GuestHostName));
        Assert.False(inventory.VirtualMachines[1].PoweredOn);
        Assert.Contains("ContinueRetrievePropertiesEx", fake.Calls);
        Assert.Equal("Logout", fake.Calls[^1]);
        // Nur lesende Aufrufe.
        Assert.All(fake.Calls, c => Assert.Contains(c, new[] { "RetrieveServiceContent", "Login", "CreateContainerView", "RetrievePropertiesEx", "ContinueRetrievePropertiesEx", "Logout" }));
    }

    [Fact]
    public async Task A_rejected_login_and_a_plain_http_address_give_clear_messages()
    {
        var error = await Assert.ThrowsAsync<RemoteException>(() => new VCenterSource(_ => new FakeVCenter(rejectLogin: true)).ReadAsync(Connection()));
        Assert.Equal("The vCenter rejected the user name or password.", error.Message);
        var http = await Assert.ThrowsAsync<RemoteException>(() => new VCenterSource(_ => new FakeVCenter()).ReadAsync(Connection("http://vcenter.corp.example")));
        Assert.Contains("https://", http.Message);
    }

    [Theory]
    [InlineData("aa:bb:cc 01", "AABBCC01")]
    [InlineData("  ", null)]
    public void Thumbprints_are_normalized(string input, string? expected) => Assert.Equal(expected, Inventory.Core.CertificateThumbprints.Normalize(input));

    private sealed class FixedSource(HypervisorInventory inventory, HypervisorKind kind = HypervisorKind.VCenter) : IHypervisorSource
    {
        public HypervisorKind Kind => kind;
        public HypervisorInventory Inventory { get; set; } = inventory;
        public Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default) => Task.FromResult(Inventory);
    }

    private static InventoryData VmwareGuest(string name, string? uuid) => new()
    {
        ComputerName = name, Manufacturer = "VMware, Inc.", Model = "VMware7,1",
        Licensing = new LicensingInfo { BiosUuid = uuid, HyperVGuest = false, CpuSockets = 2, CpuCoresPerSocket = 2, SqlInstances = [], ClusterName = "", HyperVHost = false },
    };

    [Fact]
    public async Task Synchronization_matches_vms_to_computers_by_uuid_or_name_and_follows_changes()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var credentials = new CredentialService(db, new ReversingProtector());
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("vm-sql01\nvm-app01\nvm-nouuid\npc-01")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        // Windows meldet die UUID mit vertauschter Bytefolge gegenüber dem vCenter.
        const string vcenterUuid = "421c4d6e-1234-5678-9abc-def012345678";
        await store.SaveAsync(ids["vm-sql01"], VmwareGuest("VM-SQL01", BiosUuids.Swapped(BiosUuids.Normalize(vcenterUuid)!)));
        await store.SaveAsync(ids["vm-app01"], VmwareGuest("VM-APP01", Guid.NewGuid().ToString()));
        await store.SaveAsync(ids["vm-nouuid"], VmwareGuest("VM-NOUUID", null));

        var source = new FixedSource(new HypervisorInventory(
            [new("host-11", "esx01", "PROD", 2, 32, null, true), new("host-12", "esx02", "PROD", 2, 32, null, true)],
            [
                new("vm-1", "VM-SQL01", vcenterUuid, 8, "host-11", true, "vm-sql01.corp.example", null),
                new("vm-2", "VM-APP01", Guid.NewGuid().ToString(), 4, "host-12", true, null, null),
                new("vm-3", "something", Guid.NewGuid().ToString(), 2, "host-12", true, "vm-nouuid.corp.example", null),
                new("vm-4", "appliance", Guid.NewGuid().ToString(), 2, "host-12", true, null, null),
            ], "test"));
        var credential = await credentials.SaveAsync(null, "vCenter", "reader@vsphere.local", "secret", null);
        var sync = new HypervisorSyncService(db, [source], settings, credentials, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => sync.SaveSettingsAsync(HypervisorKind.VCenter, true, "http://vcenter", credential.Id, null, 6));
        await sync.SaveSettingsAsync(HypervisorKind.VCenter, true, "https://vcenter.corp.example/", credential.Id, "aa:bb", 6);
        var result = await sync.SyncAsync(HypervisorKind.VCenter);
        Assert.Equal((2, 1, 4, 3), (result.Hosts, result.Clusters, result.VirtualMachines, result.Matched));
        Assert.Equal(("https://vcenter.corp.example", "AABB"), ((await sync.GetSettingsAsync(HypervisorKind.VCenter)).Url, (await sync.GetSettingsAsync(HypervisorKind.VCenter)).Thumbprint));

        var hosts = new VirtualizationService(db);
        var views = await hosts.HostsAsync();
        Assert.Equal(["esx01", "esx02"], views.Select(h => h.Name));
        var sql = (await computers.GetAsync(ids["vm-sql01"]))!;
        Assert.Equal(HostSources.Hypervisor, sql.HostSource);
        Assert.Equal("esx01", (await hosts.HypervisorHostAsync(sql.HypervisorHostId!.Value))!.Name);
        Assert.Equal(HostSources.HypervisorName, (await computers.GetAsync(ids["vm-app01"]))!.HostSource);
        Assert.Equal(HostSources.HypervisorName, (await computers.GetAsync(ids["vm-nouuid"]))!.HostSource);
        Assert.Null((await computers.GetAsync(ids["pc-01"]))!.HypervisorHostId);
        Assert.Empty(await hosts.GuestsWithoutHostAsync());

        // vMotion und eine verschwundene VM: der nächste Abgleich zieht nach.
        source.Inventory = source.Inventory with
        {
            VirtualMachines = [source.Inventory.VirtualMachines[0] with { HostId = "host-12" }, source.Inventory.VirtualMachines[2], source.Inventory.VirtualMachines[3]],
        };
        await sync.SyncAsync(HypervisorKind.VCenter);
        Assert.Equal("esx02", (await hosts.HypervisorHostAsync((await computers.GetAsync(ids["vm-sql01"]))!.HypervisorHostId!.Value))!.Name);
        var app = (await computers.GetAsync(ids["vm-app01"]))!;
        Assert.Equal((null, null), (app.HypervisorHostId, app.HostSource));
        Assert.Equal(["VM-APP01"], (await hosts.GuestsWithoutHostAsync()).Select(c => c.Name.ToUpperInvariant()));

        // Ein dritter Abgleich ohne Änderungen behält alle Zuordnungen (die VMs sind in der Datenbank wirklich verknüpft).
        await sync.SyncAsync(HypervisorKind.VCenter);
        await sync.SyncAsync(HypervisorKind.VCenter);
        var views2 = await hosts.HostsAsync();
        Assert.Equal(2, views2.Sum(h => h.VirtualMachines.Count(v => v.GuestComputerId is not null)));

        // Ein neuer Scan des Gasts behält die Zuordnung.
        await store.SaveAsync(ids["vm-sql01"], VmwareGuest("VM-SQL01", BiosUuids.Swapped(BiosUuids.Normalize(vcenterUuid)!)));
        Assert.Equal(HostSources.Hypervisor, (await computers.GetAsync(ids["vm-sql01"]))!.HostSource);
    }

    [Fact]
    public async Task A_failed_synchronization_is_recorded_and_keeps_the_previous_data()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var credentials = new CredentialService(db, new ReversingProtector());
        var sync = new HypervisorSyncService(db, [new FailingSource()], settings, credentials, TimeProvider.System);
        var error = await Assert.ThrowsAsync<RemoteException>(() => sync.SyncAsync(HypervisorKind.VCenter));
        Assert.Equal("Please enter the address first.", error.Message);
        Assert.StartsWith("Failed:", (await sync.GetSettingsAsync(HypervisorKind.VCenter)).LastResult);
        // Ausgeschaltet läuft nichts von selbst.
        Assert.Null(await sync.SyncIfDueAsync(HypervisorKind.VCenter));
    }

    private sealed class FailingSource : IHypervisorSource
    {
        public HypervisorKind Kind => HypervisorKind.VCenter;
        public Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default) => throw new RemoteException("unreachable");
    }

    [Fact]
    public async Task A_guest_appears_under_only_one_host_when_hyper_v_and_vcenter_both_claim_it()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var credentials = new CredentialService(db, new ReversingProtector());
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("vm-a\nhv-01")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        const string uuid = "4C4C4544-0042-3510-8052-B4C04F4E3732";
        await store.SaveAsync(ids["vm-a"], VmwareGuest("VM-A", uuid));
        var credential = await credentials.SaveAsync(null, "vCenter", "reader@vsphere.local", "secret", null);
        var sync = new HypervisorSyncService(db, [new FixedSource(new HypervisorInventory([new("host-1", "esx01", null, 2, 32, null, true)],
            [new("vm-1", "VM-A", uuid, 4, "host-1", true, null, null)], null))], settings, credentials, TimeProvider.System);
        await sync.SaveSettingsAsync(HypervisorKind.VCenter, true, "https://vcenter", credential.Id, null, 6);
        await sync.SyncAsync(HypervisorKind.VCenter);
        Assert.Equal(HostSources.Hypervisor, (await computers.GetAsync(ids["vm-a"]))!.HostSource);

        // Die VM taucht (z. B. nach einer Migration) auf einem Hyper-V-Host auf: der gewinnt, der vCenter-Verweis wird gelöst.
        await store.SaveAsync(ids["hv-01"], new InventoryData
        {
            ComputerName = "HV-01",
            Licensing = new LicensingInfo { HyperVHost = true, SqlInstances = [], VirtualMachines = [new HostedVmItem { Name = "VM-A", BiosUuid = uuid, Running = true }] },
        });
        var guest = (await computers.GetAsync(ids["vm-a"]))!;
        Assert.Equal((ids["hv-01"], (int?)null, HostSources.BiosUuid), (guest.HostComputerId, guest.HypervisorHostId, guest.HostSource));
        var views = await new VirtualizationService(db).HostsAsync();
        Assert.Equal(1, views.Sum(h => h.VirtualMachines.Count(v => v.GuestComputerId == ids["vm-a"])));
        // Auch ein weiterer vCenter-Abgleich nimmt sie Hyper-V nicht weg.
        await sync.SyncAsync(HypervisorKind.VCenter);
        Assert.Equal(ids["hv-01"], (await computers.GetAsync(ids["vm-a"]))!.HostComputerId);
        Assert.Equal(1, (await new VirtualizationService(db).HostsAsync()).Sum(h => h.VirtualMachines.Count(v => v.GuestComputerId == ids["vm-a"])));
    }

    /// <summary>Beantwortet die XAPI-Aufrufe (JSON-RPC) aus festen Daten; der erste Host antwortet als Pool-Mitglied, nicht als Master.</summary>
    private sealed class FakeXapi(bool slaveFirst = false, bool rejectLogin = false) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var method = body["method"]!.ToString();
            Calls.Add($"{request.RequestUri!.Host}:{method}");
            string Error(string code, params string[] data) =>
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":1,\"message\":\"" + code + "\",\"data\":[" + string.Join(",", data.Select(d => "\"" + d + "\"")) + "]}}";
            string Result(string json) => "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + json + "}";
            var answer = method switch
            {
                "session.login_with_password" when slaveFirst && request.RequestUri.Host == "xcp02.corp.example" => Error("HOST_IS_SLAVE", "xcp01.corp.example"),
                "session.login_with_password" when rejectLogin => Error("SESSION_AUTHENTICATION_FAILED", "root", "Authentication failure"),
                "session.login_with_password" => Result("\"OpaqueRef:session\""),
                "session.logout" => Result("\"\""),
                "pool.get_all_records" => Result("""{"OpaqueRef:pool":{"name_label":"XCP-POOL","master":"OpaqueRef:h1"}}"""),
                "host.get_all_records" => Result("""
                    {"OpaqueRef:h1":{"uuid":"aaaa-1","name_label":"xcp01","hostname":"xcp01.corp.example","enabled":true,
                       "cpu_info":{"cpu_count":"48","socket_count":"2","threads_per_core":"2"},"bios_strings":{"system-product-name":"ProLiant DL360"},
                       "software_version":{"product_brand":"XCP-ng","product_version":"8.3.0"}},
                     "OpaqueRef:h2":{"uuid":"aaaa-2","name_label":"xcp02","enabled":false,"cpu_info":{"cpu_count":"16","socket_count":"1"}}}
                    """),
                "VM.get_all_records" => Result("""
                    {"OpaqueRef:v1":{"uuid":"4c4c4544-0042-3510-8052-b4c04f4e3732","name_label":"XEN-APP01","power_state":"Running","resident_on":"OpaqueRef:h1",
                       "VCPUs_max":"4","is_a_template":false,"is_a_snapshot":false,"is_control_domain":false,"guest_metrics":"OpaqueRef:g1"},
                     "OpaqueRef:v2":{"uuid":"t-1","name_label":"Windows Server 2022 template","is_a_template":true},
                     "OpaqueRef:v3":{"uuid":"d-0","name_label":"Control domain","is_control_domain":true},
                     "OpaqueRef:v4":{"uuid":"5d5d","name_label":"XEN-OFF","power_state":"Halted","resident_on":"OpaqueRef:NULL","VCPUs_max":"2","guest_metrics":"OpaqueRef:NULL"}}
                    """),
                "VM_guest_metrics.get_all_records" => Result("""{"OpaqueRef:g1":{"os_version":{"name":"Microsoft Windows Server 2022 Standard|C:\\Windows|\\Device\\Harddisk0\\Partition2","hostname":"XEN-APP01"}}}"""),
                _ => throw new InvalidOperationException(method),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Xcp_ng_hosts_pool_and_machines_are_read_from_the_pool_master()
    {
        var fake = new FakeXapi(slaveFirst: true);
        var inventory = await new XcpNgSource(_ => fake).ReadAsync(Connection("https://xcp02.corp.example"));

        Assert.Equal("XCP-ng 8.3.0", inventory.Version);
        // Kerne = logische CPUs / Threads je Kern; ohne diese Angabe bleiben sie unbekannt.
        Assert.Equal([new HypervisorHostInfo("aaaa-1", "xcp01.corp.example", "XCP-POOL", 2, 24, "ProLiant DL360", true), new HypervisorHostInfo("aaaa-2", "xcp02", "XCP-POOL", 1, null, null, false)],
            inventory.Hosts);
        Assert.Equal(["XEN-APP01", "XEN-OFF"], inventory.VirtualMachines.Select(v => v.Name));
        var app = inventory.VirtualMachines[0];
        Assert.Equal(("4c4c4544-0042-3510-8052-b4c04f4e3732", 4, "aaaa-1", true, "XEN-APP01", "Microsoft Windows Server 2022 Standard"),
            (app.BiosUuid, app.VirtualCpus!.Value, app.HostId, app.PoweredOn, app.GuestHostName, app.GuestOs));
        Assert.Equal((null, false), (inventory.VirtualMachines[1].HostId, inventory.VirtualMachines[1].PoweredOn));
        // Weitergeleitet zum Master, dort angemeldet, nur lesende Aufrufe, am Ende abgemeldet.
        Assert.Equal("xcp02.corp.example:session.login_with_password", fake.Calls[0]);
        Assert.Equal("xcp01.corp.example:session.logout", fake.Calls[^1]);
        Assert.All(fake.Calls.Select(c => c.Split(':')[1]), m => Assert.True(m.StartsWith("session.") || m.EndsWith(".get_all_records"), m));

        var error = await Assert.ThrowsAsync<RemoteException>(() => new XcpNgSource(_ => new FakeXapi(rejectLogin: true)).ReadAsync(Connection("https://xcp01.corp.example")));
        Assert.Equal("XCP-ng rejected the user name or password.", error.Message);
    }

    [Fact]
    public async Task Vcenter_and_xcp_ng_are_synchronized_side_by_side()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var credentials = new CredentialService(db, new ReversingProtector());
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("vm-a\nxen-b")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        const string vmwareUuid = "421c4d6e-1234-5678-9abc-def012345678";
        const string xenUuid = "4c4c4544-0042-3510-8052-b4c04f4e3732";
        await store.SaveAsync(ids["vm-a"], VmwareGuest("VM-A", BiosUuids.Swapped(BiosUuids.Normalize(vmwareUuid)!)));
        await store.SaveAsync(ids["xen-b"], VmwareGuest("XEN-B", xenUuid));
        var credential = await credentials.SaveAsync(null, "reader", "reader", "secret", null);
        // Beide Verwaltungen benutzen dieselbe Kennung "h1": die Datensätze dürfen sich nicht überschreiben.
        var sync = new HypervisorSyncService(db,
        [
            new FixedSource(new HypervisorInventory([new("h1", "esx01", "PROD", 2, 32, null, true)], [new("v1", "VM-A", vmwareUuid, 4, "h1", true, null, null)], null)),
            new FixedSource(new HypervisorInventory([new("h1", "xcp01", "POOL", 2, 24, null, true)], [new("v1", "XEN-B", xenUuid, 4, "h1", true, null, null)], null), HypervisorKind.XcpNg),
        ], settings, credentials, TimeProvider.System);
        Assert.Equal([HypervisorKind.VCenter, HypervisorKind.XcpNg], sync.Kinds);
        foreach (var kind in sync.Kinds)
        {
            await sync.SaveSettingsAsync(kind, true, "https://manager", credential.Id, null, 6);
            Assert.Equal(1, (await sync.SyncAsync(kind)).Matched);
        }
        // Ein zweiter Abgleich des vCenters lässt den XCP-ng-Pool und seine Zuordnung stehen.
        await sync.SyncAsync(HypervisorKind.VCenter);
        var hosts = await new VirtualizationService(db).HostsAsync();
        Assert.Equal([("esx01", VirtualHostSource.VCenter, "VM-A"), ("xcp01", VirtualHostSource.XcpNg, "XEN-B")],
            hosts.Select(h => (h.Name, h.Source, Assert.Single(h.VirtualMachines).Name)).OrderBy(h => h.Item1));
        Assert.All(hosts, h => Assert.NotNull(h.VirtualMachines[0].GuestComputerId));
        Assert.Equal("xcp01", (await new VirtualizationService(db).HypervisorHostAsync((await computers.GetAsync(ids["xen-b"]))!.HypervisorHostId!.Value))!.Name);
        Assert.Null((await sync.GetSettingsAsync(HypervisorKind.XcpNg)).Thumbprint);
        Assert.StartsWith("1 hosts", (await sync.GetSettingsAsync(HypervisorKind.XcpNg)).LastResult);
    }

    /// <summary>Beantwortet die REST-Abfragen von Xen Orchestra; ohne das richtige Token-Cookie gibt es 401.</summary>
    private sealed class FakeXenOrchestra : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (!request.Headers.TryGetValues("Cookie", out var cookies) || !cookies.Contains("authenticationToken=tok-123"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var json = request.RequestUri.AbsolutePath switch
            {
                "/rest/v0/pools" => """[{"id":"pool-1","name_label":"XCP-POOL"}]""",
                "/rest/v0/hosts" => """
                    [{"id":"aaaa-1","name_label":"xcp01","hostname":"xcp01.corp.example","enabled":true,"$pool":"pool-1",
                      "CPUs":{"cpu_count":"48","socket_count":"2","cores_per_socket":"12"},"bios_strings":{"system-product-name":"ProLiant DL360"},
                      "productBrand":"XCP-ng","version":"8.3.0"}]
                    """,
                "/rest/v0/vms" => """
                    [{"id":"4c4c4544-0042-3510-8052-b4c04f4e3732","name_label":"XEN-APP01","power_state":"Running","$container":"aaaa-1",
                      "CPUs":{"max":4,"number":4},"os_version":{"name":"Microsoft Windows Server 2022 Standard|C:\\Windows"}},
                     {"id":"5d5d","name_label":"XEN-OFF","power_state":"Halted","$container":"pool-1","CPUs":{"max":2,"number":2}}]
                    """,
                _ => "[]",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Xen_orchestra_is_read_with_an_authentication_token()
    {
        var fake = new FakeXenOrchestra();
        var token = new HypervisorConnection("https://xo.corp.example", new NetworkCredential("token", "tok-123"), null);
        var inventory = await new XenOrchestraSource(_ => fake).ReadAsync(token);

        Assert.Equal("Xen Orchestra · XCP-ng 8.3.0", inventory.Version);
        Assert.Equal([new HypervisorHostInfo("aaaa-1", "xcp01.corp.example", "XCP-POOL", 2, 24, "ProLiant DL360", true)], inventory.Hosts);
        var app = inventory.VirtualMachines[0];
        Assert.Equal(("XEN-APP01", "4c4c4544-0042-3510-8052-b4c04f4e3732", 4, "aaaa-1", true, "Microsoft Windows Server 2022 Standard"),
            (app.Name, app.BiosUuid, app.VirtualCpus!.Value, app.HostId, app.PoweredOn, app.GuestOs));
        // Eine angehaltene VM hängt am Pool, nicht an einem Host.
        Assert.Equal((null, false), (inventory.VirtualMachines[1].HostId, inventory.VirtualMachines[1].PoweredOn));
        Assert.All(fake.Requests, r => Assert.StartsWith("GET /rest/v0/", r));

        var rejected = await Assert.ThrowsAsync<RemoteException>(() => new XenOrchestraSource(_ => new FakeXenOrchestra())
            .ReadAsync(token with { Credential = new NetworkCredential("token", "wrong") }));
        Assert.Equal("Xen Orchestra rejected the authentication token.", rejected.Message);
        await Assert.ThrowsAsync<RemoteException>(() => new XenOrchestraSource(_ => new FakeXenOrchestra())
            .ReadAsync(token with { Credential = new NetworkCredential("token", "") }));
    }
}
