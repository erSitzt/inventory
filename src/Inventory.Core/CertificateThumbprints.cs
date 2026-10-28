namespace Inventory.Core;

/// <summary>Fingerabdrücke von Zertifikaten in einer vergleichbaren Schreibweise.</summary>
public static class CertificateThumbprints
{
    /// <summary>Großbuchstaben ohne Trennzeichen, damit eingefügte Fingerabdrücke mit Doppelpunkten oder Leerzeichen passen; leer = null.</summary>
    public static string? Normalize(string? thumbprint)
    {
        var hex = new string((thumbprint ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 0 ? null : hex;
    }
}
