using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Zone corp.example mit den Reverse-Zonen für 10.0.0–10.0.9 und 10.20.0 im Simulationsmodus. Je Netz: antwortende Rechner mit
/// A und PTR, .40–.44 nur mit statischem A-Eintrag (ausgeschaltet), .50–.59 mit verwaistem PTR, .60–.62 mit lange nicht
/// aufgefrischtem A-Eintrag.
/// </summary>
public sealed class SimulatedDnsZoneSource : IDnsZoneSource
{
    private const string Zone = "corp.example";
    private static readonly string[] Networks = [.. Enumerable.Range(0, 10).Select(n => $"10.0.{n}"), "10.20.0"];

    public Task<DnsZoneData> ReadAsync(DnsZoneQuery query, CancellationToken ct = default)
    {
        var records = Records();
        var a = records.Where(r => r.Forward).Select(r => r.Record).ToList();
        var ptr = records.Where(r => r.Reverse).Select(r => r.Record).ToList();
        return Task.FromResult(new DnsZoneData(a, ptr, 1 + Networks.Length));
    }

    private static List<(DnsZoneRecord Record, bool Forward, bool Reverse)> Records()
    {
        var now = DateTime.UtcNow;
        var result = new List<(DnsZoneRecord, bool, bool)>();
        foreach (var network in Networks)
        {
            var tag = network.Replace('.', '-');
            for (var octet = 1; octet < 255; octet++)
            {
                var ip = $"{network}.{octet}";
                if (SimulatedAddressProbe.Answers(octet) && octet < 150)
                    result.Add((new DnsZoneRecord(Host(tag, octet), ip, octet < 20 ? null : now.AddDays(-(octet % 6))), true, true));
                else if (SimulatedAddressProbe.Answers(octet))
                    result.Add((new DnsZoneRecord($"client-{tag}-{octet}.{Zone}", ip, now.AddHours(-octet % 30)), true, true));
                else if (octet is >= 40 and <= 44)
                    result.Add((new DnsZoneRecord($"srv-off-{tag}-{octet}.{Zone}", ip, null), true, false));
                else if (octet is >= 50 and <= 59)
                    result.Add((new DnsZoneRecord($"old-{tag}-{octet}.{Zone}", ip, now.AddDays(-400 - octet)), false, true));
                else if (octet is >= 60 and <= 62)
                    result.Add((new DnsZoneRecord($"retired-{tag}-{octet}.{Zone}", ip, now.AddDays(-200)), true, true));
            }
        }
        return result;
    }

    private static string Host(string tag, int octet) => octet switch
    {
        1 => $"gw-{tag}.{Zone}",
        2 or 3 => $"sw-{tag}-{octet - 1}.{Zone}",
        10 or 11 => $"dc-{tag}-{octet - 9}.{Zone}",
        _ => $"srv-{tag}-{octet}.{Zone}",
    };

    internal static string? PointerName(string ip) => Records().FirstOrDefault(r => r.Reverse && r.Record.Address == ip).Record?.Name;

    internal static IReadOnlyList<string> Resolve(string name) =>
        [.. Records().Where(r => r.Forward && string.Equals(r.Record.Name, name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)).Select(r => r.Record.Address)];
}
