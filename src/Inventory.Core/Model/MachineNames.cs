namespace Inventory.Core.Model;

/// <summary>Vergleich zwischen eingetragenem Namen und dem Namen, mit dem sich ein Rechner selbst meldet.</summary>
public static class MachineNames
{
    /// <summary>NetBIOS-Namen sind auf 15 Zeichen begrenzt; so meldet sich ein Rechner mit längerem DNS-Namen.</summary>
    private const int NetbiosNameLength = 15;

    /// <summary>Der Name, unter dem sich der Rechner melden müsste; null, wenn eine IP-Adresse eingetragen ist.</summary>
    public static string? Expected(string entered) =>
        System.Net.IPAddress.TryParse(entered, out _) ? null : Label(entered);

    /// <summary>true, wenn ein Name bekannt ist und nicht zum Eintrag passt. Unbekannt (null) gilt nicht als Abweichung.</summary>
    public static bool Differs(string entered, string? actual) =>
        actual is not null && Expected(entered) is { } expected && !string.Equals(expected, Label(actual), StringComparison.OrdinalIgnoreCase);

    private static string Label(string name)
    {
        var label = name.Split('.')[0];
        return label.Length > NetbiosNameLength ? label[..NetbiosNameLength] : label;
    }
}
