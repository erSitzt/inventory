using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Inventory.Remoting;

public sealed record MachineIdentity(string? NetbiosName, string? DnsName);

/// <summary>
/// Fragt einen Windows-Rechner ohne Anmeldung nach seinem eigenen Namen: Beim Beginn einer SMB-Anmeldung
/// schickt der Server eine NTLM-Challenge, die seinen NetBIOS- und DNS-Namen enthält. Damit lässt sich erkennen,
/// ob unter einer Adresse wirklich der erwartete Rechner antwortet (veraltete DNS-Einträge, Aliase).
/// </summary>
public static class SmbNameQuery
{
    private const int SmbPort = 445;
    private const int HeaderSize = 64;
    private const ushort CommandNegotiate = 0;
    private const ushort CommandSessionSetup = 1;
    private static readonly byte[] NtlmSignature = "NTLMSSP\0"u8.ToArray();

    // NTLM-Typ-1-Nachricht, verpackt in SPNEGO (NegTokenInit mit Mechanismus NTLMSSP).
    private static readonly byte[] SecurityBlob =
    [
        0x60, 0x40, 0x06, 0x06, 0x2b, 0x06, 0x01, 0x05, 0x05, 0x02, 0xa0, 0x36, 0x30, 0x34,
        0xa0, 0x0e, 0x30, 0x0c, 0x06, 0x0a, 0x2b, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0a,
        0xa2, 0x22, 0x04, 0x20,
        (byte)'N', (byte)'T', (byte)'L', (byte)'M', (byte)'S', (byte)'S', (byte)'P', 0x00,
        0x01, 0x00, 0x00, 0x00, // Typ 1
        0x07, 0x82, 0x08, 0xe0, // Flags: Unicode, OEM, Request Target, NTLM, Always Sign, Extended Security, 128, Key Exchange, 56
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Domäne (leer)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Arbeitsstation (leer)
    ];

    /// <summary>Liefert null, wenn der Rechner nicht antwortet oder kein Windows-typisches SMB spricht.</summary>
    public static async Task<MachineIdentity?> QueryAsync(IPAddress address, TimeSpan timeout, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, SmbPort, limit.Token);
            var stream = client.GetStream();

            await SendAsync(stream, CommandNegotiate, messageId: 0, NegotiateBody(), limit.Token);
            await ReceiveAsync(stream, limit.Token);
            await SendAsync(stream, CommandSessionSetup, messageId: 1, SessionSetupBody(), limit.Token);
            return ParseChallenge(await ReceiveAsync(stream, limit.Token));
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static byte[] NegotiateBody()
    {
        var body = new byte[36 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), 36);     // StructureSize
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), 2);      // DialectCount
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1);      // SecurityMode: Signing enabled
        Guid.NewGuid().TryWriteBytes(body.AsSpan(12));                    // ClientGuid
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(36), 0x0202); // SMB 2.0.2
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(38), 0x0210); // SMB 2.1
        return body;
    }

    private static byte[] SessionSetupBody()
    {
        var body = new byte[24 + SecurityBlob.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), 25);     // StructureSize
        body[3] = 1;                                                      // SecurityMode: Signing enabled
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), HeaderSize + 24);              // SecurityBufferOffset
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)SecurityBlob.Length);  // SecurityBufferLength
        SecurityBlob.CopyTo(body.AsSpan(24));
        return body;
    }

    private static async Task SendAsync(NetworkStream stream, ushort command, ulong messageId, byte[] body, CancellationToken ct)
    {
        var packet = new byte[4 + HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0), HeaderSize + body.Length); // NetBIOS-Sitzungskopf: Länge
        var header = packet.AsSpan(4);
        header[0] = 0xFE; header[1] = (byte)'S'; header[2] = (byte)'M'; header[3] = (byte)'B';
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 1);          // CreditCharge
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], command);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 1);         // CreditRequest
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], messageId);
        body.CopyTo(packet.AsSpan(4 + HeaderSize));
        await stream.WriteAsync(packet, ct);
    }

    private static async Task<byte[]> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix) & 0x00FFFFFF;
        if (length is < HeaderSize or > 1 << 20)
            throw new InvalidDataException("Unerwartete SMB-Antwort");
        var message = new byte[length];
        await stream.ReadExactlyAsync(message, ct);
        return message;
    }

    /// <summary>Sucht die NTLM-Challenge (Typ 2) in der Antwort und liest daraus die Namen des Rechners.</summary>
    internal static MachineIdentity? ParseChallenge(ReadOnlySpan<byte> response)
    {
        var start = response.IndexOf(NtlmSignature);
        if (start < 0)
            return null;
        var ntlm = response[start..];
        if (ntlm.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(ntlm[8..]) != 2)
            return null;

        int infoLength = BinaryPrimitives.ReadUInt16LittleEndian(ntlm[40..]);
        var infoOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ntlm[44..]);
        if (infoLength == 0 || infoOffset + infoLength > ntlm.Length)
            return null;

        string? netbios = null, dns = null;
        var info = ntlm.Slice(infoOffset, infoLength);
        while (info.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(info);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(info[2..]);
            if (id == 0 || 4 + length > info.Length)
                break;
            var value = Encoding.Unicode.GetString(info.Slice(4, length));
            if (id == 1)
                netbios = value;      // MsvAvNbComputerName
            else if (id == 3)
                dns = value;          // MsvAvDnsComputerName
            info = info[(4 + length)..];
        }
        return netbios is null && dns is null ? null : new MachineIdentity(netbios, dns);
    }
}
