using System.Net;
using System.Net.Sockets;
using Inventory.Remoting.Devices;

namespace Inventory.Tests;

/// <summary>Ein SNMP-Agent auf localhost, der GET und GETNEXT aus einer festen Tabelle beantwortet.</summary>
internal sealed class FakeSnmpAgent : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();

    public FakeSnmpAgent(string community, Dictionary<string, (byte Type, byte[] Data)> values)
    {
        var ordered = values.OrderBy(v => v.Key.Split('.').Select(int.Parse).ToArray(), Comparer<int[]>.Create((a, b) =>
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i])
                    return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        })).ToList();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try
                {
                    request = await _socket.ReceiveAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                var message = new Ber.Reader(request.Buffer).Expect(0x30);
                var version = Ber.ReadSigned(message.Expect(SnmpValue.Integer).Rest());
                if (System.Text.Encoding.UTF8.GetString(message.Expect(SnmpValue.OctetString).Rest()) != community)
                    continue;
                var (pduType, pdu) = message.Next();
                var id = Ber.ReadSigned(pdu.Expect(SnmpValue.Integer).Rest());
                pdu.Expect(SnmpValue.Integer);
                pdu.Expect(SnmpValue.Integer);
                var list = pdu.Expect(0x30);
                var bindings = new List<byte>();
                while (!list.AtEnd)
                {
                    var oid = Ber.ReadOid(list.Expect(0x30).Expect(SnmpValue.ObjectId).Rest());
                    var (name, type, data) = pduType == SnmpMessage.GetNextRequest
                        ? ordered.SkipWhile(v => !After(v.Key, oid)).Select(v => (v.Key, v.Value.Type, v.Value.Data)).DefaultIfEmpty((oid, SnmpValue.EndOfMibView, [])).First()
                        : values.TryGetValue(oid, out var value) ? (oid, value.Type, value.Data) : (oid, SnmpValue.NoSuchObject, []);
                    bindings.AddRange(Ber.Tlv(0x30, [.. Ber.Tlv(SnmpValue.ObjectId, Ber.EncodeOid(name)), .. Ber.Tlv(type, data)]));
                }
                var response = Ber.Tlv(0x30,
                [
                    .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(version)),
                    .. Ber.Tlv(SnmpValue.OctetString, System.Text.Encoding.UTF8.GetBytes(community)),
                    .. Ber.Tlv(SnmpMessage.Response,
                    [
                        .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(id)),
                        .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(0)),
                        .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(0)),
                        .. Ber.Tlv(0x30, [.. bindings]),
                    ]),
                ]);
                await _socket.SendAsync(response, request.RemoteEndPoint);
            }
        });
    }

    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    private static bool After(string candidate, string oid)
    {
        var a = candidate.Split('.').Select(int.Parse).ToArray();
        var b = oid.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i])
                return a[i] > b[i];
        return a.Length > b.Length;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
    }
}
