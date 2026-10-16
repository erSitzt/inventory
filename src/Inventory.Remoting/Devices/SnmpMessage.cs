using System.Globalization;
using System.Text;

namespace Inventory.Remoting.Devices;

/// <summary>Ein Wert aus einer SNMP-Antwort.</summary>
public sealed record SnmpValue(string Oid, byte Type, byte[] Data)
{
    public const byte Integer = 0x02;
    public const byte OctetString = 0x04;
    public const byte Null = 0x05;
    public const byte ObjectId = 0x06;
    public const byte IpAddress = 0x40;
    public const byte Counter32 = 0x41;
    public const byte Gauge32 = 0x42;
    public const byte TimeTicks = 0x43;
    public const byte Counter64 = 0x46;
    public const byte NoSuchObject = 0x80;
    public const byte NoSuchInstance = 0x81;
    public const byte EndOfMibView = 0x82;

    /// <summary>Das Gerät kennt den Wert nicht (v2c-Ausnahmen oder leerer Wert).</summary>
    public bool Missing => Type is Null or NoSuchObject or NoSuchInstance or EndOfMibView;

    public long? AsNumber() => Type switch
    {
        Integer => Ber.ReadSigned(Data),
        Counter32 or Gauge32 or TimeTicks or Counter64 => (long)Ber.ReadUnsigned(Data),
        _ => null,
    };

    /// <summary>Text eines OCTET STRING; nicht druckbare Bytes (z. B. eine MAC-Adresse) als Hex mit Doppelpunkten.</summary>
    public string? AsText()
    {
        switch (Type)
        {
            case OctetString:
                var end = Data.Length;
                while (end > 0 && Data[end - 1] == 0)
                    end--;
                // Bytes ab 0x80 gehören zu UTF-8-Zeichen (Umlaute im Standort); Steuerzeichen außer Zeilenumbrüchen deuten auf Binärdaten.
                if (Data.Take(end).All(b => b >= 0x20 && b != 0x7F || b is 0x09 or 0x0A or 0x0D))
                    return Encoding.UTF8.GetString(Data, 0, end).Trim();
                return string.Join(":", Data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            case ObjectId:
                return Ber.ReadOid(Data);
            case IpAddress when Data.Length == 4:
                return string.Join(".", Data);
            default:
                return AsNumber()?.ToString(CultureInfo.InvariantCulture);
        }
    }

    public byte[] AsBytes() => Data;
}

/// <summary>Kodiert SNMP-Anfragen (GET, GETNEXT) und liest Antworten; nur v1/v2c, nur was der Gerätescan braucht.</summary>
public static class SnmpMessage
{
    public const byte GetRequest = 0xA0;
    public const byte GetNextRequest = 0xA1;
    public const byte Response = 0xA2;

    public static byte[] Encode(int version, string community, byte pdu, int requestId, IEnumerable<string> oids)
    {
        var bindings = oids.Select(oid => Ber.Tlv(0x30, [.. Ber.Tlv(SnmpValue.ObjectId, Ber.EncodeOid(oid)), .. Ber.Tlv(SnmpValue.Null, [])]));
        var body = Ber.Tlv(pdu,
        [
            .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(requestId)),
            .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(0)),
            .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(0)),
            .. Ber.Tlv(0x30, [.. bindings.SelectMany(b => b)]),
        ]);
        return Ber.Tlv(0x30,
        [
            .. Ber.Tlv(SnmpValue.Integer, Ber.EncodeInteger(version)),
            .. Ber.Tlv(SnmpValue.OctetString, Encoding.UTF8.GetBytes(community)),
            .. body,
        ]);
    }

    /// <summary>Liest eine Antwort; null, wenn sie nicht zur Anfrage passt oder nicht lesbar ist.</summary>
    /// <param name="errorStatus">Fehlerstatus des Geräts (0 = ok, 2 = noSuchName bei v1).</param>
    public static List<SnmpValue>? Decode(byte[] packet, int requestId, out int errorStatus)
    {
        errorStatus = 0;
        try
        {
            var reader = new Ber.Reader(packet);
            var message = reader.Expect(0x30);
            message.Expect(SnmpValue.Integer);
            message.Expect(SnmpValue.OctetString);
            var (pduType, pdu) = message.Next();
            if (pduType != Response)
                return null;
            if (Ber.ReadSigned(pdu.Expect(SnmpValue.Integer).Rest()) != requestId)
                return null;
            errorStatus = (int)Ber.ReadSigned(pdu.Expect(SnmpValue.Integer).Rest());
            pdu.Expect(SnmpValue.Integer);
            var list = pdu.Expect(0x30);
            var values = new List<SnmpValue>();
            while (!list.AtEnd)
            {
                var binding = list.Expect(0x30);
                var oid = Ber.ReadOid(binding.Expect(SnmpValue.ObjectId).Rest());
                var (type, value) = binding.Next();
                values.Add(new SnmpValue(oid, type, value.Rest()));
            }
            return values;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>Grundbausteine der BER-Kodierung (ASN.1), soweit SNMP sie braucht.</summary>
internal static class Ber
{
    public static byte[] Tlv(byte type, byte[] content)
    {
        var length = content.Length;
        byte[] header = length switch
        {
            < 0x80 => [type, (byte)length],
            < 0x100 => [type, 0x81, (byte)length],
            _ => [type, 0x82, (byte)(length >> 8), (byte)length],
        };
        return [.. header, .. content];
    }

    public static byte[] EncodeInteger(long value)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Insert(0, (byte)value);
            value >>= 8;
        }
        while (value != 0 && value != -1);
        // Vorzeichenbit muss zum Wert passen.
        if (value == 0 && (bytes[0] & 0x80) != 0)
            bytes.Insert(0, 0);
        else if (value == -1 && (bytes[0] & 0x80) == 0)
            bytes.Insert(0, 0xFF);
        return [.. bytes];
    }

    public static byte[] EncodeOid(string oid)
    {
        var parts = oid.TrimStart('.').Split('.').Select(p => uint.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length < 2)
            throw new FormatException($"'{oid}' is not an object identifier.");
        var bytes = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        foreach (var part in parts.Skip(2))
        {
            var chunk = new Stack<byte>();
            var v = part;
            chunk.Push((byte)(v & 0x7F));
            while ((v >>= 7) != 0)
                chunk.Push((byte)(0x80 | (v & 0x7F)));
            bytes.AddRange(chunk);
        }
        return [.. bytes];
    }

    public static string ReadOid(byte[] data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("Empty object identifier.");
        var parts = new List<ulong> { (ulong)Math.Min(data[0] / 40, 2), (ulong)(data[0] - Math.Min(data[0] / 40, 2) * 40) };
        ulong value = 0;
        for (var i = 1; i < data.Length; i++)
        {
            value = (value << 7) | (uint)(data[i] & 0x7F);
            if ((data[i] & 0x80) == 0)
            {
                parts.Add(value);
                value = 0;
            }
        }
        return string.Join(".", parts);
    }

    public static long ReadSigned(byte[] data)
    {
        if (data.Length is 0 or > 8)
            throw new InvalidDataException("Invalid integer.");
        long value = (sbyte)data[0];
        for (var i = 1; i < data.Length; i++)
            value = (value << 8) | data[i];
        return value;
    }

    public static ulong ReadUnsigned(byte[] data)
    {
        if (data.Length > 9)
            throw new InvalidDataException("Invalid counter.");
        ulong value = 0;
        foreach (var b in data)
            value = (value << 8) | b;
        return value;
    }

    public sealed class Reader(byte[] data, int start = 0, int end = -1)
    {
        private int _position = start;
        private readonly int _end = end < 0 ? data.Length : end;

        public bool AtEnd => _position >= _end;

        public (byte Type, Reader Content) Next()
        {
            if (_position + 2 > _end)
                throw new InvalidDataException("Truncated.");
            var type = data[_position++];
            int length = data[_position++];
            if (length > 0x80)
            {
                var count = length & 0x7F;
                if (count > 3 || _position + count > _end)
                    throw new InvalidDataException("Invalid length.");
                length = 0;
                for (var i = 0; i < count; i++)
                    length = (length << 8) | data[_position++];
            }
            else if (length == 0x80)
            {
                throw new InvalidDataException("Indefinite length.");
            }
            if (_position + length > _end)
                throw new InvalidDataException("Truncated.");
            var content = new Reader(data, _position, _position + length);
            _position += length;
            return (type, content);
        }

        public Reader Expect(byte type)
        {
            var (actual, content) = Next();
            return actual == type ? content : throw new InvalidDataException($"Expected 0x{type:X2}, found 0x{actual:X2}.");
        }

        public byte[] Rest() => data[_position.._end];
    }
}
