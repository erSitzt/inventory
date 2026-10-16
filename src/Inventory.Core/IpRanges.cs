using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Inventory.Core;

/// <summary>Liest die Adressangaben eines Gerätebereichs: CIDR, von–bis, einzelne Adressen (nur IPv4).</summary>
public static class IpRanges
{
    /// <summary>Obergrenze je Bereich, damit ein Tippfehler (/8 statt /24) nicht 16 Millionen Adressen abfragt.</summary>
    public const int MaxAddresses = 4096;

    /// <summary>Alle Adressen in aufsteigender Reihenfolge, ohne Doppelte. Bei CIDR-Netzen ab /30 entfallen Netz- und Broadcast-Adresse.</summary>
    /// <exception cref="FormatException">Eine Angabe ist keine gültige Adresse oder kein gültiger Bereich.</exception>
    /// <exception cref="ArgumentException">Mehr als <see cref="MaxAddresses"/> Adressen.</exception>
    public static List<IPAddress> Expand(string text)
    {
        var values = new SortedSet<uint>();
        foreach (var part in text.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var (first, last) = Parse(part);
            // Gezählt wird nach dem Zusammenführen: überlappende Angaben belasten die Grenze nicht doppelt.
            for (var v = first; ; v++)
            {
                if (values.Add(v) && values.Count > MaxAddresses)
                    throw new ArgumentException($"More than {MaxAddresses} addresses.");
                if (v == last)
                    break;
            }
        }
        return [.. values.Select(ToAddress)];
    }

    /// <summary>Anzahl der Adressen oder eine Fehlermeldung, für die Eingabeprüfung.</summary>
    public static string? Validate(string text)
    {
        try
        {
            return Expand(text).Count == 0 ? "No addresses." : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return ex.Message;
        }
    }

    private static (uint First, uint Last) Parse(string part)
    {
        var slash = part.IndexOf('/');
        if (slash > 0)
        {
            var network = ToValue(part[..slash]);
            if (!int.TryParse(part[(slash + 1)..], out var prefix) || prefix is < 20 or > 32)
                throw new FormatException($"'{part}': the prefix must be between /20 and /32.");
            var mask = uint.MaxValue << (32 - prefix);
            var first = network & mask;
            var last = first | ~mask;
            return prefix <= 30 ? (first + 1, last - 1) : (first, last);
        }

        var dash = part.IndexOf('-');
        if (dash > 0)
        {
            var from = ToValue(part[..dash]);
            var rest = part[(dash + 1)..];
            // 10.0.0.10-50: nur das letzte Oktett angegeben.
            var to = byte.TryParse(rest, out var octet) ? (from & 0xFFFFFF00) | octet : ToValue(rest);
            if (to < from)
                throw new FormatException($"'{part}': the end lies before the start.");
            return (from, to);
        }

        var single = ToValue(part);
        return (single, single);
    }

    private static uint ToValue(string text)
    {
        if (!IPAddress.TryParse(text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork || text.Count(c => c == '.') != 3)
            throw new FormatException($"'{text}' is not an IPv4 address.");
        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    private static IPAddress ToAddress(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    /// <summary>Präfixlänge einer Subnetzmaske (255.255.255.0 → 24); null, wenn die Maske ungültig ist (Lücken, kein IPv4).</summary>
    public static int? PrefixLength(string? mask)
    {
        if (!TryValue(mask, out var value))
            return null;
        var prefix = System.Numerics.BitOperations.PopCount(value);
        // Eine gültige Maske besteht aus Einsen, gefolgt von Nullen.
        return (prefix == 0 && value == 0) || value == uint.MaxValue << (32 - prefix) ? prefix : null;
    }

    /// <summary>Das Netz einer Adresse als CIDR ("10.0.0.0/24"); null bei ungültigen Angaben.</summary>
    public static string? Network(string? address, int prefix)
    {
        if (!TryValue(address, out var value) || prefix is < 0 or > 32)
            return null;
        var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return $"{ToAddress(value & mask)}/{prefix}";
    }

    /// <summary>True, wenn die Adresse im CIDR-Netz liegt.</summary>
    public static bool Contains(string cidr, string? address)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0 || !int.TryParse(cidr[(slash + 1)..], out var prefix) || prefix is < 0 or > 32
            || !TryValue(cidr[..slash], out var network) || !TryValue(address, out var value))
            return false;
        var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (value & mask) == (network & mask);
    }

    /// <summary>Präfixlänge eines CIDR-Netzes; -1, wenn es keins ist.</summary>
    public static int PrefixOf(string cidr) =>
        cidr.IndexOf('/') is > 0 and var slash && int.TryParse(cidr[(slash + 1)..], out var prefix) ? prefix : -1;

    /// <summary>Adressen, die in keinem Netzplan vorkommen: Loopback, Link-Local (APIPA), Multicast und 0.0.0.0.</summary>
    public static bool IsUsable(string? address) =>
        TryValue(address, out var value) && value != 0 && (value >> 24) != 127 && (value >> 16) != 0xA9FE && (value >> 28) != 0xE;

    private static bool TryValue(string? text, out uint value)
    {
        value = 0;
        if (text is null || text.Count(c => c == '.') != 3 || !IPAddress.TryParse(text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        return true;
    }

    /// <summary>Sortierschlüssel für Adressen in Listen (10.0.0.9 vor 10.0.0.10).</summary>
    public static long SortKey(string? address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())
            : long.MaxValue;
}
