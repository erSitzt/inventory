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

    private static string Value(string part)
    {
        var value = part[(part.IndexOf('=') + 1)..];
        return value.Replace("\\,", ",").Replace("\\\\", "\\");
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
