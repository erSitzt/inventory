namespace Inventory.Core;

/// <summary>Zerlegt Distinguished Names des Verzeichnisses (CN=PC-01,OU=Laptops,DC=corp,DC=example).</summary>
public static class DirectoryNames
{
    /// <summary>Bestandteile eines DN; ein Komma mit vorangestelltem Backslash gehört zum Wert.</summary>
    private static List<string> Parts(string dn)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\')
                i++;
            else if (dn[i] == ',')
            {
                parts.Add(dn[start..i].Trim());
                start = i + 1;
            }
        }
        if (start < dn.Length)
            parts.Add(dn[start..].Trim());
        return parts;
    }

    /// <summary>Wert eines Bestandteils ohne Maskierung (RFC 4514): \, steht für das Zeichen selbst, \2C für das Byte in UTF-8.</summary>
    private static string Value(string part)
    {
        var value = part[(part.IndexOf('=') + 1)..];
        if (!value.Contains('\\'))
            return value;
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(value[i].ToString()));
            }
            else if (i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(value[i + 1].ToString()));
                i++;
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Der Container, in dem das Objekt liegt: der DN ohne seinen ersten Bestandteil.</summary>
    public static string? Parent(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
            return null;
        var parts = Parts(dn);
        return parts.Count > 1 ? string.Join(",", parts.Skip(1)) : null;
    }

    /// <summary>Name des Objekts, z. B. der Gruppenname aus CN=Laptops,OU=Groups,…</summary>
    public static string CommonName(string dn) => Parts(dn) is [var first, ..] ? Value(first) : dn;

    /// <summary>Lesbarer Pfad von oben nach unten ohne die Domäne, z. B. "Clients/Laptops"; leer für die Domänenwurzel.</summary>
    public static string? Path(string? containerDn)
    {
        if (string.IsNullOrWhiteSpace(containerDn))
            return null;
        var names = Parts(containerDn).Where(p => !p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(Value).Reverse();
        return string.Join("/", names);
    }
}
