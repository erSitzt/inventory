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
            if ((long)last - first + 1 + values.Count > MaxAddresses)
                throw new ArgumentException($"More than {MaxAddresses} addresses.");
            for (var v = first; ; v++)
            {
                values.Add(v);
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

    /// <summary>Sortierschlüssel für Adressen in Listen (10.0.0.9 vor 10.0.0.10).</summary>
    public static long SortKey(string? address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())
            : long.MaxValue;
}
