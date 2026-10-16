using System.Net;
using Inventory.Core;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting.Devices;

/// <summary>Liest per SNMP die Angaben, aus denen sich Art, Modell und Zustand eines Geräts ergeben.</summary>
public static class SnmpCollector
{
    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysObjectId = "1.3.6.1.2.1.1.2.0";
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    private const string SysContact = "1.3.6.1.2.1.1.4.0";
    private const string SysName = "1.3.6.1.2.1.1.5.0";
    private const string SysLocation = "1.3.6.1.2.1.1.6.0";
    private const string IfNumber = "1.3.6.1.2.1.2.1.0";
    private const string IpForwarding = "1.3.6.1.2.1.4.1.0";
    private const string BridgePorts = "1.3.6.1.2.1.17.1.2.0";
    // Drucker-MIB (RFC 3805) und Host-Resources-MIB.
    private const string PrinterSerial = "1.3.6.1.2.1.43.5.1.1.17.1";
    private const string PrinterLifeCount = "1.3.6.1.2.1.43.10.2.1.4.1.1";
    private const string FirstDeviceDescr = "1.3.6.1.2.1.25.3.2.1.3.1";
    private const string SupplyDescr = "1.3.6.1.2.1.43.11.1.1.6.1";
    private const string SupplyMax = "1.3.6.1.2.1.43.11.1.1.8.1";
    private const string SupplyLevel = "1.3.6.1.2.1.43.11.1.1.9.1";
    // USV-MIB (RFC 1628).
    private const string UpsManufacturer = "1.3.6.1.2.1.33.1.1.1.0";
    private const string UpsModel = "1.3.6.1.2.1.33.1.1.2.0";
    private const string UpsFirmware = "1.3.6.1.2.1.33.1.1.3.0";
    // ENTITY-MIB: Spalten von entPhysicalTable.
    private const string EntityTable = "1.3.6.1.2.1.47.1.1.1.1";
    private const string ArpTable = "1.3.6.1.2.1.4.22.1.2";
    // Herstellereigene Angaben für Geräte, deren Standard-MIBs kein Modell nennen.
    private const string SynologyModel = "1.3.6.1.4.1.6574.1.5.1.0";
    private const string SynologySerial = "1.3.6.1.4.1.6574.1.5.2.0";
    private const string SynologyVersion = "1.3.6.1.4.1.6574.1.5.3.0";
    private const string ApcModel = "1.3.6.1.4.1.318.1.1.1.1.1.1.0";
    private const string ApcSerial = "1.3.6.1.4.1.318.1.1.1.1.2.3.0";
    private const string ApcFirmware = "1.3.6.1.4.1.318.1.1.1.1.2.1.0";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>Versucht die Communities nacheinander (erst v2c, dann v1); null, wenn keine Antwort kommt.</summary>
    public static async Task<SnmpFacts?> CollectAsync(IPAddress address, IReadOnlyList<string> communities, CancellationToken ct)
    {
        foreach (var community in communities.Where(c => c.Length > 0))
        {
            foreach (var version in new[] { SnmpClient.V2c, SnmpClient.V1 })
            {
                var client = new SnmpClient(address, community, version, Timeout);
                var system = await client.GetAsync([SysDescr, SysObjectId, SysUpTime, SysContact, SysName, SysLocation], ct);
                if (system is null || system.Count == 0)
                    continue;
                return await DetailsAsync(client, system, ct);
            }
        }
        return null;
    }

    private static async Task<SnmpFacts> DetailsAsync(SnmpClient client, Dictionary<string, SnmpValue> system, CancellationToken ct)
    {
        var facts = new SnmpFacts
        {
            Community = client.Community,
            Version = client.Version,
            SysDescr = Text(system, SysDescr),
            SysObjectId = Text(system, SysObjectId),
            SysName = Text(system, SysName),
            SysContact = Text(system, SysContact),
            SysLocation = Text(system, SysLocation),
            UptimeSeconds = system.GetValueOrDefault(SysUpTime)?.AsNumber() / 100,
        };

        var more = await client.GetAsync([IfNumber, IpForwarding, BridgePorts, PrinterSerial, PrinterLifeCount, FirstDeviceDescr, UpsManufacturer, UpsModel, UpsFirmware], ct) ?? [];
        facts.InterfaceCount = (int?)more.GetValueOrDefault(IfNumber)?.AsNumber();
        facts.Forwarding = more.GetValueOrDefault(IpForwarding)?.AsNumber() is { } forwarding ? forwarding == 1 : null;
        facts.BridgePorts = (int?)more.GetValueOrDefault(BridgePorts)?.AsNumber();

        if (more.ContainsKey(PrinterLifeCount) || more.ContainsKey(PrinterSerial))
        {
            facts.IsPrinter = true;
            facts.SerialNumber = Text(more, PrinterSerial);
            facts.PageCount = more.GetValueOrDefault(PrinterLifeCount)?.AsNumber();
            facts.Model = Text(more, FirstDeviceDescr);
            facts.Supplies = await SuppliesAsync(client, ct);
        }
        if (Text(more, UpsManufacturer) is { } upsMaker)
        {
            facts.IsUps = true;
            facts.Manufacturer = upsMaker;
            facts.Model = Text(more, UpsModel);
            facts.Firmware = Text(more, UpsFirmware);
        }

        var enterprise = DeviceClassifier.Enterprise(facts.SysObjectId);
        if (enterprise == 6574)
            await VendorAsync(client, facts, SynologyModel, SynologySerial, SynologyVersion, ct);
        else if (enterprise == 318)
            await VendorAsync(client, facts, ApcModel, ApcSerial, ApcFirmware, ct);
        if (facts.Model is null || facts.SerialNumber is null)
            await EntityAsync(client, facts, ct);
        if (facts.Forwarding == true)
            facts.ArpTable = await ArpAsync(client, ct);
        return facts;
    }

    private static async Task VendorAsync(SnmpClient client, SnmpFacts facts, string model, string serial, string firmware, CancellationToken ct)
    {
        var values = await client.GetAsync([model, serial, firmware], ct) ?? [];
        facts.Model ??= Text(values, model);
        facts.SerialNumber ??= Text(values, serial);
        facts.Firmware ??= Text(values, firmware);
    }

    private static async Task<List<SupplyFacts>> SuppliesAsync(SnmpClient client, CancellationToken ct)
    {
        var descriptions = await client.WalkAsync(SupplyDescr, 32, ct);
        if (descriptions.Count == 0)
            return [];
        var max = (await client.WalkAsync(SupplyMax, 32, ct)).ToDictionary(v => Index(v.Oid), v => (int)(v.AsNumber() ?? -1));
        var level = (await client.WalkAsync(SupplyLevel, 32, ct)).ToDictionary(v => Index(v.Oid), v => (int)(v.AsNumber() ?? -1));
        return [.. descriptions
            .Where(d => !string.IsNullOrWhiteSpace(d.AsText()))
            .Select(d => new SupplyFacts(d.AsText()!, level.GetValueOrDefault(Index(d.Oid), -1), max.GetValueOrDefault(Index(d.Oid), -1)))];
    }

    /// <summary>Modell, Seriennummer, Hersteller und Firmware des Gehäuses (entPhysicalClass = chassis) oder des ersten Eintrags.</summary>
    private static async Task EntityAsync(SnmpClient client, SnmpFacts facts, CancellationToken ct)
    {
        var classes = await client.WalkAsync(EntityTable + ".5", 64, ct);
        if (classes.Count == 0)
            return;
        var index = (classes.FirstOrDefault(c => c.AsNumber() == 3) ?? classes[0]).Oid[(EntityTable.Length + 3)..];
        string Column(int column) => $"{EntityTable}.{column}.{index}";
        var values = await client.GetAsync([Column(2), Column(10), Column(11), Column(12), Column(13)], ct) ?? [];
        facts.Model ??= Text(values, Column(13)) ?? Text(values, Column(2));
        facts.SerialNumber ??= Text(values, Column(11));
        facts.Manufacturer ??= Text(values, Column(12));
        facts.Firmware ??= Text(values, Column(10));
    }

    /// <summary>ipNetToMediaPhysAddress: Schlüssel ifIndex.a.b.c.d, Wert die MAC-Adresse.</summary>
    private static async Task<Dictionary<string, string>> ArpAsync(SnmpClient client, CancellationToken ct)
    {
        var table = new Dictionary<string, string>();
        foreach (var entry in await client.WalkAsync(ArpTable, IpRanges.MaxAddresses, ct))
        {
            var parts = entry.Oid[(ArpTable.Length + 1)..].Split('.');
            if (parts.Length != 5 || entry.AsBytes().Length != 6)
                continue;
            if (WakeOnLan.NormalizeMac(Convert.ToHexString(entry.AsBytes())) is { } mac)
                table[string.Join(".", parts[1..])] = mac;
        }
        return table;
    }

    private static string Index(string oid) => oid[(oid.LastIndexOf('.') + 1)..];

    private static string? Text(Dictionary<string, SnmpValue> values, string oid) =>
        values.GetValueOrDefault(oid)?.AsText() is { Length: > 0 } text ? text : null;
}
