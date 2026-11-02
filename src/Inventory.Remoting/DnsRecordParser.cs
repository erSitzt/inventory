using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Inventory.Remoting;

/// <summary>Ein gelesener Eintrag aus dem Attribut dnsRecord.</summary>
/// <param name="Value">A: die Adresse, PTR: der Zielname ohne abschließenden Punkt.</param>
/// <param name="RefreshedUtc">Zeitpunkt der letzten dynamischen Registrierung; null = statisch.</param>
internal readonly record struct DnsRecordValue(ushort Type, string Value, DateTime? RefreshedUtc);

/// <summary>
/// Liest das binäre Attribut dnsRecord AD-integrierter Zonen ([MS-DNSP] 2.3.2.2): 24 Bytes Kopf (Länge, Typ, Version 5, Rang,
/// Flags, Serial, TTL, Reserve, Zeitstempel in Stunden seit 1601), danach die Daten. Nur A und PTR; alles andere ergibt null.
/// </summary>
internal static class DnsRecordParser
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    private const int HeaderLength = 24;
    private static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DnsRecordValue? Parse(byte[] data)
    {
        if (data.Length < HeaderLength || data[4] != 5)
            return null;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        if (data.Length < HeaderLength + length)
            return null;
        var hours = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        DateTime? refreshed = hours == 0 ? null : Epoch.AddHours(hours);
        var body = data.AsSpan(HeaderLength, length);
        return type switch
        {
            TypeA when body.Length == 4 => new DnsRecordValue(type, new IPAddress(body).ToString(), refreshed),
            TypePtr when CountName(body) is { } name => new DnsRecordValue(type, name, refreshed),
            _ => null,
        };
    }

    /// <summary>DNS_COUNT_NAME: Gesamtlänge, Zahl der Labels, dann je Label Länge und Zeichen, abgeschlossen mit 0.</summary>
    private static string? CountName(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2)
            return null;
        var labels = new List<string>();
        var position = 2;
        for (var i = 0; i < body[1]; i++)
        {
            if (position >= body.Length)
                return null;
            var size = body[position++];
            if (size == 0 || position + size > body.Length)
                return null;
            labels.Add(Encoding.UTF8.GetString(body.Slice(position, size)));
            position += size;
        }
        return labels.Count == 0 ? null : string.Join('.', labels);
    }

    /// <summary>Die Adresse zu einem Namen in einer Reverse-Zone ("5.1" in "0.10.in-addr.arpa" → 10.0.1.5); null bei anderen Namen.</summary>
    public static string? ReverseAddress(string node, string zone)
    {
        const string suffix = ".in-addr.arpa";
        if (!zone.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        var full = (node == "@" ? "" : node + ".") + zone[..^suffix.Length];
        var parts = full.Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, out _)) ? string.Join('.', parts.Reverse()) : null;
    }
}
