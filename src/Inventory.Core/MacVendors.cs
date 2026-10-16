using System.Collections.Frozen;
using System.IO.Compression;

namespace Inventory.Core;

/// <summary>Hersteller zu einer MAC-Adresse laut dem öffentlichen MA-L-Register der IEEE (aktualisiert mit scripts/update-oui.py).</summary>
public static class MacVendors
{
    private static readonly Lazy<FrozenDictionary<string, string>> Table = new(Load);

    /// <summary>Hersteller oder null, wenn das Präfix nicht vergeben ist oder die Adresse lokal verwaltet wird (zufällige MAC von Telefonen).</summary>
    public static string? Lookup(string? mac)
    {
        if (WakeOnLan.ParseMac(mac) is not { } bytes || (bytes[0] & 0x02) != 0)
            return null;
        return Table.Value.GetValueOrDefault(Convert.ToHexString(bytes, 0, 3));
    }

    private static FrozenDictionary<string, string> Load()
    {
        using var stream = typeof(MacVendors).Assembly.GetManifestResourceStream("Inventory.Core.oui.txt.gz");
        if (stream is null)
            return FrozenDictionary<string, string>.Empty;
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab == 6)
                table[line[..6]] = line[7..];
        }
        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
