using System.Net;
using System.Net.Sockets;

namespace Inventory.Remoting.Devices;

/// <summary>SNMP v1/v2c über UDP: GET und ein einfacher Walk mit GETNEXT.</summary>
public sealed class SnmpClient(IPAddress address, string community, int version, TimeSpan timeout, int port = 161)
{
    public const int V1 = 0;
    public const int V2c = 1;
    private static int _requestId = Random.Shared.Next(1, int.MaxValue / 2);

    public string Community => community;
    public int Version => version;

    /// <summary>Fragt mehrere Werte auf einmal ab. Ergebnis enthält nur die vorhandenen; null = keine Antwort.</summary>
    public async Task<Dictionary<string, SnmpValue>?> GetAsync(IReadOnlyList<string> oids, CancellationToken ct)
    {
        var values = await RequestAsync(SnmpMessage.GetRequest, oids, ct);
        if (values is null)
            return null;
        if (values.Errored && version == V1 && oids.Count > 1)
        {
            // v1 bricht bei einem unbekannten Wert die ganze Anfrage ab (noSuchName); dann einzeln nachfragen.
            var single = new Dictionary<string, SnmpValue>();
            foreach (var oid in oids)
            {
                var one = await RequestAsync(SnmpMessage.GetRequest, [oid], ct);
                if (one is { Errored: false })
                    foreach (var v in one.Values.Where(v => !v.Missing))
                        single[v.Oid] = v;
            }
            return single;
        }
        return values.Errored ? [] : values.Values.Where(v => !v.Missing).ToDictionary(v => v.Oid);
    }

    /// <summary>Alle Werte unterhalb von <paramref name="root"/>, höchstens <paramref name="limit"/> Stück.</summary>
    public async Task<List<SnmpValue>> WalkAsync(string root, int limit, CancellationToken ct)
    {
        var result = new List<SnmpValue>();
        var prefix = root + ".";
        var current = root;
        while (result.Count < limit)
        {
            var response = await RequestAsync(SnmpMessage.GetNextRequest, [current], ct);
            if (response is null || response.Errored || response.Values.Count == 0)
                break;
            var value = response.Values[0];
            if (value.Missing || !value.Oid.StartsWith(prefix, StringComparison.Ordinal) || value.Oid == current)
                break;
            result.Add(value);
            current = value.Oid;
        }
        return result;
    }

    private sealed record Reply(List<SnmpValue> Values, bool Errored);

    private async Task<Reply?> RequestAsync(byte pdu, IReadOnlyList<string> oids, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId) & int.MaxValue;
        var packet = SnmpMessage.Encode(version, community, pdu, id, oids);
        using var client = new UdpClient(address.AddressFamily);
        client.Connect(address, port);
        // Ein verlorenes Paket ist bei UDP normal; ein zweiter Versuch genügt im LAN.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await client.SendAsync(packet, limit.Token);
                while (true)
                {
                    var received = await client.ReceiveAsync(limit.Token);
                    var values = SnmpMessage.Decode(received.Buffer, id, out var error);
                    if (values is not null)
                        return new Reply(values, error != 0);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Zeitüberschreitung: noch einmal senden.
            }
            catch (SocketException)
            {
                // ICMP "Port unreachable": hier läuft kein SNMP.
                return null;
            }
        }
        return null;
    }
}
