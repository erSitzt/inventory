using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public class LicensingDataTests
{
    private const string GuestUuid = "4C4C4544-0042-3510-8052-B4C04F4E3732";

    [Theory]
    [InlineData("OEM:DM", null, "OEM:DM")]
    [InlineData("volume:gvlk", null, "Volume:GVLK")]
    [InlineData("OEM:COA", null, "OEM")]
    [InlineData(null, "Windows(R) Operating System, VOLUME_KMSCLIENT channel", "Volume:GVLK")]
    [InlineData(null, "Windows(R) Operating System, VOLUME_MAK channel", "Volume:MAK")]
    [InlineData(null, "Windows(R) Operating System, OEM_SLP channel", "OEM:SLP")]
    [InlineData(null, "Windows(R) Operating System, RETAIL channel", "Retail")]
    [InlineData(null, "Windows(R) Operating System", null)]
    public void Licence_channels_are_normalized(string? channel, string? description, string? expected) =>
        Assert.Equal(expected, LicenseChannels.Normalize(channel, description));

    [Fact]
    public void Bios_uuids_compare_with_braces_case_and_swapped_byte_order()
    {
        Assert.Equal(GuestUuid, BiosUuids.Normalize("{" + GuestUuid.ToLowerInvariant() + "}"));
        Assert.Null(BiosUuids.Normalize("00000000-0000-0000-0000-000000000000"));
        Assert.Null(BiosUuids.Normalize("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"));
        Assert.Null(BiosUuids.Normalize("not a uuid"));
        Assert.Equal("44454C4C-4200-1035-8052-B4C04F4E3732", BiosUuids.Swapped(GuestUuid));
        Assert.True(BiosUuids.Same(GuestUuid, "44454c4c-4200-1035-8052-b4c04f4e3732"));
        Assert.False(BiosUuids.Same(GuestUuid, null));
    }

    private static InventoryData Host(string name, params HostedVmItem[] vms) => new()
    {
        ComputerName = name,
        Licensing = new LicensingInfo
        {
            CpuSockets = 2, CpuCoresPerSocket = 16, BiosUuid = Guid.NewGuid().ToString(), OsProductType = 3, OsEdition = "ServerDatacenter",
            ServerFeatures = [new ServerFeatureItem { Id = ServerFeatures.HyperV, Name = "Hyper-V" }], SqlInstances = [], VirtualMachines = [.. vms],
        },
    };

    private static InventoryData Guest(string name, string? uuid, string? reportedHost = null) => new()
    {
        ComputerName = name,
        Licensing = new LicensingInfo
        {
            CpuSockets = 1, CpuCoresPerSocket = 4, BiosUuid = uuid, HyperVGuest = reportedHost is not null, ReportedHostName = reportedHost,
            OsProductType = 3, OsEdition = "ServerStandard", LicenseChannel = LicenseChannels.VolumeGvlk,
            SqlInstances = [new SqlInstanceItem { Name = "MSSQLSERVER", Edition = "Standard Edition (64-bit)", Version = "16.0.4135.4" }],
        },
    };

    /// <summary>Zwei geklonte VMs mit derselben BIOS-UUID: die Namen entscheiden, sonst stünden die Gäste über Kreuz.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cloned_virtual_machines_with_the_same_bios_uuid_are_told_apart_by_name(bool guestsFirst)
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("hv-01\nsrv-dc01\nsrv-dc02")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        // Die zweite VM in der Liste des Hosts ist dc01: Wer der Reihe nach nimmt, landet bei der falschen.
        var host = Host("HV-01", new HostedVmItem { Name = "SRV-DC02", BiosUuid = GuestUuid, Running = true },
            new HostedVmItem { Name = "SRV-DC01", BiosUuid = GuestUuid, Running = true });

        if (!guestsFirst)
            await store.SaveAsync(ids["hv-01"], host);
        await store.SaveAsync(ids["srv-dc01"], Guest("SRV-DC01", GuestUuid));
        await store.SaveAsync(ids["srv-dc02"], Guest("SRV-DC02", GuestUuid));
        if (guestsFirst)
            await store.SaveAsync(ids["hv-01"], host);

        var vms = (await computers.GetDetailAsync(ids["hv-01"]))!.VirtualMachines.ToDictionary(v => v.Name, v => v.GuestComputerId);
        Assert.Equal((ids["srv-dc01"], ids["srv-dc02"]), (vms["SRV-DC01"], vms["SRV-DC02"]));
        foreach (var dc in new[] { "srv-dc01", "srv-dc02" })
        {
            var guest = (await computers.GetAsync(ids[dc]))!;
            Assert.Equal((ids["hv-01"], HostSources.BiosUuid), (guest.HostComputerId, guest.HostSource));
        }
    }

    [Fact]
    public async Task A_shared_bios_uuid_without_a_matching_vm_name_links_nothing()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("hv-01\nsrv-a\nsrv-b")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        await store.SaveAsync(ids["srv-a"], Guest("SRV-A", GuestUuid));
        await store.SaveAsync(ids["srv-b"], Guest("SRV-B", GuestUuid));
        await store.SaveAsync(ids["hv-01"], Host("HV-01", new HostedVmItem { Name = "Template copy 1", BiosUuid = GuestUuid, Running = true },
            new HostedVmItem { Name = "Template copy 2", BiosUuid = GuestUuid, Running = true }));

        Assert.All((await computers.GetDetailAsync(ids["hv-01"]))!.VirtualMachines, v => Assert.Null(v.GuestComputerId));
        Assert.Null((await computers.GetAsync(ids["srv-a"]))!.HostComputerId);
        // Eine einzelne VM mit anderem Namen und ein einzelner Rechner: wie bisher über die UUID allein.
        Assert.Null((await computers.GetAsync(ids["srv-b"]))!.HostSource);
    }

    [Fact]
    public async Task VCenter_clones_with_the_same_bios_uuid_are_told_apart_by_name()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("srv-dc01\nsrv-dc02")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var dc in new[] { "SRV-DC01", "SRV-DC02" })
        {
            var guest = Guest(dc, GuestUuid);
            guest.Model = "VMware7,1";
            await store.SaveAsync(ids[dc], guest);
        }
        await using (var context = db.CreateDbContext())
        {
            var manager = new HypervisorManager { Name = "vcenter1", Url = "https://vcenter1.example" };
            context.HypervisorManagers.Add(manager);
            await context.SaveChangesAsync();
            var esx = new HypervisorHost { ManagerId = manager.Id, ExternalId = "host-1", Name = "esx01", Connected = true };
            context.HypervisorHosts.Add(esx);
            await context.SaveChangesAsync();
            // Bisher gewann je UUID die zuletzt gelesene VM: dc02, und die bekam dann dc01.
            foreach (var name in new[] { "SRV-DC01", "SRV-DC02" })
                context.HypervisorVms.Add(new HypervisorVm { ManagerId = manager.Id, ExternalId = name, Name = name, BiosUuid = GuestUuid, HostId = esx.Id, PoweredOn = true });
            await context.SaveChangesAsync();
            await VirtualHosts.ResolveVCenterAsync(context);
        }

        await using var check = db.CreateDbContext();
        var vms = await check.HypervisorVms.ToDictionaryAsync(v => v.Name, v => v.GuestComputerId);
        Assert.Equal((ids["srv-dc01"], ids["srv-dc02"]), (vms["SRV-DC01"], vms["SRV-DC02"]));
    }

    [Fact]
    public async Task Virtual_machines_find_their_host_by_bios_uuid_in_either_scan_order()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var ids = (await computers.ImportAsync("hv-01\nhv-02\nsrv-sql01\nsrv-app01")).ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);

        // Gast vor dem Host: erst ohne Zuordnung, mit dem Scan des Hosts dann über die UUID (in vertauschter Bytefolge gemeldet).
        await store.SaveAsync(ids["srv-sql01"], Guest("SRV-SQL01", GuestUuid));
        Assert.Null((await computers.GetAsync(ids["srv-sql01"]))!.HostComputerId);
        await store.SaveAsync(ids["hv-01"], Host("HV-01", new HostedVmItem { Name = "SRV-SQL01", BiosUuid = "{" + BiosUuids.Swapped(GuestUuid) + "}", VirtualCpus = 8, Running = true }));
        var guest = (await computers.GetDetailAsync(ids["srv-sql01"]))!;
        Assert.Equal((ids["hv-01"], HostSources.BiosUuid), (guest.HostComputerId, guest.HostSource));
        Assert.Equal("ServerStandard", guest.OsEdition);
        Assert.Single(guest.SqlInstances);
        var host = (await computers.GetDetailAsync(ids["hv-01"]))!;
        Assert.Equal(ids["srv-sql01"], Assert.Single(host.VirtualMachines).GuestComputerId);
        Assert.Equal((2, 16), (host.CpuSockets!.Value, host.CpuCoresPerSocket!.Value));

        // Live-Migration: der andere Host meldet die VM, der bisherige nicht mehr.
        await store.SaveAsync(ids["hv-02"], Host("HV-02", new HostedVmItem { Name = "SRV-SQL01", BiosUuid = GuestUuid, Running = true }));
        await store.SaveAsync(ids["hv-01"], Host("HV-01"));
        Assert.Equal(ids["hv-02"], (await computers.GetAsync(ids["srv-sql01"]))!.HostComputerId);

        // Host vor dem Gast und Gast ohne UUID-Treffer: der Name aus den Gastdiensten genügt, wenn der Host inventarisiert ist.
        await store.SaveAsync(ids["srv-app01"], Guest("SRV-APP01", Guid.NewGuid().ToString(), "HV-01.corp.example"));
        var app = (await computers.GetAsync(ids["srv-app01"]))!;
        Assert.Equal((ids["hv-01"], HostSources.GuestReport), (app.HostComputerId, app.HostSource));
        Assert.True(app.IsVirtual);

        // Ein Gast, dessen Hostname sich diesmal nicht lesen ließ, behält den bisherigen.
        var nameless = Guest("SRV-APP01", Guid.NewGuid().ToString(), "HV-01.corp.example");
        nameless.Licensing!.ReportedHostName = null;
        await store.SaveAsync(ids["srv-app01"], nameless);
        Assert.Equal("HV-01.corp.example", (await computers.GetAsync(ids["srv-app01"]))!.ReportedHostName);

        // Wird der Host gelöscht, ist die VM nur nicht mehr zugeordnet.
        await computers.DeleteAsync([ids["hv-02"]]);
        Assert.Null((await computers.GetAsync(ids["srv-sql01"]))!.HostComputerId);
    }

    [Fact]
    public async Task Older_runners_and_unreadable_values_keep_the_licensing_data()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("srv-sql01"))[0].Id;
        await store.SaveAsync(id, Guest("SRV-SQL01", GuestUuid));
        await store.SaveAsync(id, new InventoryData { ComputerName = "SRV-SQL01" });
        var kept = (await computers.GetDetailAsync(id))!;
        Assert.Equal((LicenseChannels.VolumeGvlk, 1, GuestUuid), (kept.LicenseChannel, kept.CpuSockets!.Value, kept.BiosUuid));
        Assert.Single(kept.SqlInstances);

        // Lizenzangaben ohne lesbare SQL-Liste und ohne Kanal: die bisherigen bleiben.
        var partial = Guest("SRV-SQL01", GuestUuid);
        partial.Licensing!.SqlInstances = null;
        partial.Licensing.LicenseChannel = null;
        partial.Licensing.OsEdition = "ServerDatacenter";
        await store.SaveAsync(id, partial);
        var after = (await computers.GetDetailAsync(id))!;
        Assert.Equal((LicenseChannels.VolumeGvlk, "ServerDatacenter"), (after.LicenseChannel, after.OsEdition));
        Assert.Single(after.SqlInstances);
        var change = Assert.Single(await store.ChangesAsync(id), c => c.Change.Item == "Windows edition").Change;
        Assert.Equal(("ServerStandard", "ServerDatacenter"), (change.OldValue, change.NewValue));
    }

    [Fact]
    public async Task Hyper_v_hosts_keep_their_machines_and_cluster_when_they_could_not_be_read()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("hv-01"))[0].Id;
        var first = Host("HV-01", new HostedVmItem { Name = "SRV-A", BiosUuid = GuestUuid });
        first.Licensing!.ClusterName = "HVCLUSTER01";
        await store.SaveAsync(id, first);
        var unreadable = Host("HV-01");
        unreadable.Licensing!.VirtualMachines = null;
        unreadable.Licensing.HyperVHost = null;
        unreadable.Licensing.ClusterName = null;
        await store.SaveAsync(id, unreadable);
        var kept = (await computers.GetDetailAsync(id))!;
        Assert.Single(kept.VirtualMachines);
        Assert.Equal("HVCLUSTER01", kept.ClusterName);
        unreadable.Licensing.ClusterName = "";
        await store.SaveAsync(id, unreadable);
        Assert.Null((await computers.GetAsync(id))!.ClusterName);

        // Erst wenn es Hyper-V auf dem Rechner sicher nicht (mehr) gibt, ist er kein Host mehr.
        unreadable.Licensing.HyperVHost = false;
        await store.SaveAsync(id, unreadable);
        Assert.Empty((await computers.GetDetailAsync(id))!.VirtualMachines);
    }

    [Fact]
    public void Collection_rules_see_licensing_fields()
    {
        var computer = new Computer
        {
            Name = "srv-sql02", LicensingScanUtc = DateTime.UtcNow, HyperVGuest = true, LicenseChannel = LicenseChannels.VolumeMak, OsEdition = "ServerStandard", CpuSockets = 2,
            SqlInstances = [new ComputerSqlInstance { Name = "MSSQLSERVER", Edition = "Enterprise Edition: Core-based Licensing (64-bit)" }],
            ServerFeatures = [new ComputerServerFeature { FeatureId = ServerFeatures.RemoteDesktopSessionHost, Name = "Remote Desktop Session Host" }],
        };
        bool Match(RuleField field, RuleOperator op, string value) => CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, computer);

        Assert.True(Match(RuleField.IsVirtual, RuleOperator.Equals, "true"));
        Assert.True(Match(RuleField.LicenseChannel, RuleOperator.StartsWith, "Volume"));
        Assert.True(Match(RuleField.OsEdition, RuleOperator.Equals, "ServerStandard"));
        Assert.True(Match(RuleField.SqlInstance, RuleOperator.Contains, "Enterprise"));
        Assert.False(Match(RuleField.SqlInstance, RuleOperator.Contains, "Developer"));
        Assert.True(Match(RuleField.SqlInstance, RuleOperator.NotContains, "Developer"));
        Assert.True(Match(RuleField.ServerFeature, RuleOperator.Contains, "Session Host"));
        Assert.True(Match(RuleField.CpuSockets, RuleOperator.GreaterOrEqual, "2"));

        // Ohne Scan mit Lizenzangaben ist "keine VM" keine Aussage.
        var unknown = new Computer { Name = "pc-01" };
        Assert.False(CollectionEvaluator.Matches(new CollectionRule { Field = RuleField.IsVirtual, Operator = RuleOperator.Equals, Value = "false" }, unknown));
    }

    [Fact]
    public void The_simulation_places_server_vms_on_the_hyper_v_cluster()
    {
        var host = SimulatedInventory.Create("hv-01").Licensing!;
        var other = SimulatedInventory.Create("hv-02").Licensing!;
        var vms = host.VirtualMachines!.Concat(other.VirtualMachines!).ToList();
        Assert.Equal(7, vms.Count);
        Assert.Equal("HVCLUSTER01", host.ClusterName);
        var sql = SimulatedInventory.Create("srv-sql02").Licensing!;
        Assert.Contains(vms, v => v.Name == "SRV-SQL02" && BiosUuids.Same(v.BiosUuid, sql.BiosUuid));
        Assert.Equal(2, sql.SqlInstances!.Count);
        var client = SimulatedInventory.Create("pc-01").Licensing!;
        Assert.Equal((1, (bool?)false), (client.OsProductType, client.HyperVGuest));
        Assert.NotNull(client.LicenseChannel);
    }
}
