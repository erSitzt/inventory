using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Inventory.Core;

/// <summary>Wake-on-LAN: das Magic Packet und die Adressen, an die es für einen Rechner geht.</summary>
public static class WakeOnLan
{
    /// <summary>Discard-Port; die Netzwerkkarte wertet nur den Inhalt des Pakets aus.</summary>
    public const int Port = 9;

    /// <summary>Liest eine MAC-Adresse mit oder ohne Trennzeichen (00:1A:2B:3C:4D:5E, 00-1A-…, 001A.2B3C.4D5E); null, wenn sie keine ist.</summary>
    public static byte[]? ParseMac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var hex = text.Trim().Replace(":", "").Replace("-", "").Replace(".", "");
        if (hex.Length != 12)
            return null;
        var mac = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mac[i]))
                return null;
        }
        // Nur Nullen oder nur FF ist keine Karte, sondern ein Platzhalter bzw. die Broadcast-Adresse.
        return mac.All(b => b == 0) || mac.All(b => b == 0xFF) ? null : mac;
    }

    /// <summary>Einheitliche Schreibweise 00:1A:2B:3C:4D:5E; null, wenn der Text keine MAC-Adresse ist.</summary>
    public static string? NormalizeMac(string? text) => ParseMac(text) is { } mac ? Format(mac) : null;

    public static string Format(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Sechsmal FF, danach sechzehnmal die MAC-Adresse.</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6)
            throw new ArgumentException("A MAC address has six bytes.", nameof(mac));
        var packet = new byte[6 + 16 * 6];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
            mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    /// <summary>
    /// Broadcast-Adresse des Subnetzes einer IPv4-Adresse; ohne brauchbare Maske wird /24 angenommen.
    /// null, wenn die Adresse keine IPv4-Adresse ist.
    /// </summary>
    public static IPAddress? Broadcast(string? address, string? mask = null)
    {
        if (!IPAddress.TryParse(address?.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var maskBytes = IPAddress.TryParse(mask?.Trim(), out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && mask!.Contains('.')
            ? parsed.GetAddressBytes()
            : [255, 255, 255, 0];
        var bytes = ip.GetAddressBytes();
        for (var i = 0; i < 4; i++)
            bytes[i] |= (byte)~maskBytes[i];
        return new IPAddress(bytes);
    }

    /// <summary>Ein bekannter Netzwerkadapter eines Rechners; Adressen und Masken stehen in derselben Reihenfolge.</summary>
    public sealed record Adapter(string? Mac, IReadOnlyList<string> Addresses, IReadOnlyList<string> Masks);

    /// <summary>Was für einen Rechner zu senden ist.</summary>
    public sealed record Plan(IReadOnlyList<byte[]> Macs, IReadOnlyList<IPAddress> Addresses);

    /// <summary>
    /// Stellt zusammen, welche MAC-Adressen geweckt und wohin die Pakete geschickt werden: an die Broadcast-Adressen der
    /// zuletzt bekannten Subnetze (über Router hinweg nur, wenn diese gerichtete Broadcasts weiterleiten) und an die
    /// zuletzt bekannten Adressen selbst (hilft, solange Switch und Router den Rechner noch kennen).
    /// Eine von Hand eingetragene MAC-Adresse ersetzt die der Adapter.
    /// </summary>
    public static Plan PlanFor(string? wakeMac, string? lastAddress, IEnumerable<Adapter> adapters)
    {
        var macs = new List<byte[]>();
        var addresses = new List<IPAddress>();
        void Mac(byte[]? mac)
        {
            if (mac is not null && !macs.Any(m => m.AsSpan().SequenceEqual(mac)))
                macs.Add(mac);
        }
        void Address(IPAddress? address)
        {
            if (address is not null && address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !addresses.Contains(address))
                addresses.Add(address);
        }

        var manual = ParseMac(wakeMac);
        Mac(manual);
        var masks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (manual is null)
                Mac(ParseMac(adapter.Mac));
            for (var i = 0; i < adapter.Addresses.Count; i++)
            {
                var address = adapter.Addresses[i].Trim();
                if (i < adapter.Masks.Count)
                    masks[address] = adapter.Masks[i];
                Address(Broadcast(address, masks.GetValueOrDefault(address)));
            }
        }
        if (!string.IsNullOrWhiteSpace(lastAddress))
            Address(Broadcast(lastAddress, masks.GetValueOrDefault(lastAddress.Trim())));
        // Die Adressen selbst erst nach den Broadcasts: die sind der verlässlichere Weg.
        foreach (var adapter in adapters)
        {
            foreach (var address in adapter.Addresses)
                Address(IPAddress.TryParse(address.Trim(), out var ip) ? ip : null);
        }
        Address(IPAddress.TryParse(lastAddress?.Trim(), out var last) ? last : null);
        return new Plan(macs, addresses);
    }
}
