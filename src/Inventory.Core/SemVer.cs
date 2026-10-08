using Inventory.Contracts;

namespace Inventory.Core;

public static class SemVer
{
    /// <summary>
    /// Vergleicht Versionen wie 1.2.0 und 1.2.0-rc1. Anders als bei einem rein numerischen Vergleich ist eine
    /// Vorabversion älter als die fertige Version mit derselben Nummer.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);
        var core = VersionComparer.Compare(leftCore, rightCore);
        if (core != 0)
            return core;
        if (leftPre is null || rightPre is null)
            return leftPre is null ? (rightPre is null ? 0 : 1) : -1;
        var pre = VersionComparer.Compare(leftPre, rightPre);
        return pre != 0 ? pre : Math.Sign(string.CompareOrdinal(leftPre, rightPre));
    }

    private static (string Core, string? PreRelease) Split(string? version)
    {
        // Ein vorangestelltes v (Tag-Name) und Build-Metadaten hinter + spielen für die Reihenfolge keine Rolle.
        var text = (version ?? "").Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = text.IndexOf('-');
        return dash < 0 ? (text, null) : (text[..dash], text[(dash + 1)..]);
    }
}
