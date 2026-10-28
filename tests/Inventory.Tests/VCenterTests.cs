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

    private sealed class FixedSource(HypervisorInventory inventory) : IHypervisorSource
    {
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
        var sync = new VCenterSyncService(db, source, settings, credentials, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => sync.SaveSettingsAsync(true, "http://vcenter", credential.Id, null, 6));
        await sync.SaveSettingsAsync(true, "https://vcenter.corp.example/", credential.Id, "aa:bb", 6);
        var result = await sync.SyncAsync();
        Assert.Equal((2, 1, 4, 3), (result.Hosts, result.Clusters, result.VirtualMachines, result.Matched));
        Assert.Equal(("https://vcenter.corp.example", "AABB"), ((await sync.GetSettingsAsync()).Url, (await sync.GetSettingsAsync()).Thumbprint));

        var hosts = new VirtualizationService(db);
        var views = await hosts.HostsAsync();
        Assert.Equal(["esx01", "esx02"], views.Select(h => h.Name));
        var sql = (await computers.GetAsync(ids["vm-sql01"]))!;
        Assert.Equal(HostSources.VCenter, sql.HostSource);
        Assert.Equal("esx01", (await hosts.HypervisorHostAsync(sql.HypervisorHostId!.Value))!.Name);
        Assert.Equal(HostSources.VCenterName, (await computers.GetAsync(ids["vm-app01"]))!.HostSource);
        Assert.Equal(HostSources.VCenterName, (await computers.GetAsync(ids["vm-nouuid"]))!.HostSource);
        Assert.Null((await computers.GetAsync(ids["pc-01"]))!.HypervisorHostId);
        Assert.Empty(await hosts.GuestsWithoutHostAsync());

        // vMotion und eine verschwundene VM: der nächste Abgleich zieht nach.
        source.Inventory = source.Inventory with
        {
            VirtualMachines = [source.Inventory.VirtualMachines[0] with { HostId = "host-12" }, source.Inventory.VirtualMachines[2], source.Inventory.VirtualMachines[3]],
        };
        await sync.SyncAsync();
        Assert.Equal("esx02", (await hosts.HypervisorHostAsync((await computers.GetAsync(ids["vm-sql01"]))!.HypervisorHostId!.Value))!.Name);
        var app = (await computers.GetAsync(ids["vm-app01"]))!;
        Assert.Equal((null, null), (app.HypervisorHostId, app.HostSource));
        Assert.Equal(["VM-APP01"], (await hosts.GuestsWithoutHostAsync()).Select(c => c.Name.ToUpperInvariant()));

        // Ein neuer Scan des Gasts behält die Zuordnung.
        await store.SaveAsync(ids["vm-sql01"], VmwareGuest("VM-SQL01", BiosUuids.Swapped(BiosUuids.Normalize(vcenterUuid)!)));
        Assert.Equal(HostSources.VCenter, (await computers.GetAsync(ids["vm-sql01"]))!.HostSource);
    }

    [Fact]
    public async Task A_failed_synchronization_is_recorded_and_keeps_the_previous_data()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var credentials = new CredentialService(db, new ReversingProtector());
        var sync = new VCenterSyncService(db, new FailingSource(), settings, credentials, TimeProvider.System);
        var error = await Assert.ThrowsAsync<RemoteException>(() => sync.SyncAsync());
        Assert.Equal("Please enter the vCenter address first.", error.Message);
        Assert.StartsWith("Failed:", (await sync.GetSettingsAsync()).LastResult);
        // Ausgeschaltet läuft nichts von selbst.
        Assert.Null(await sync.SyncIfDueAsync());
    }

    private sealed class FailingSource : IHypervisorSource
    {
        public Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default) => throw new RemoteException("unreachable");
    }
}
