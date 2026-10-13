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

    /// <summary>
    /// Unter welchem Namen der Rechner im Netz angesprochen wird: der volle DNS-Name, wenn einer bekannt ist. Ein Eintrag,
    /// der selbst schon ein DNS-Name oder eine Adresse ist, gilt so, wie er eingetragen wurde.
    /// </summary>
    public static string Address(string entered, string? dnsName) =>
        string.IsNullOrWhiteSpace(dnsName) || entered.Contains('.') || System.Net.IPAddress.TryParse(entered, out _) ? entered : dnsName;

    /// <summary>
    /// Der DNS-Name, wenn er plausibel zum Rechner gehört: ein Hostname mit Domäne, dessen erster Teil der Rechnername ist.
    /// Sonst null – so lenkt weder ein falscher Eintrag im Verzeichnis noch ein sich anmeldender Agent Verbindungen auf einen anderen Rechner.
    /// </summary>
    public static string? ValidDnsName(string? dnsName, string machineName)
    {
        var name = dnsName?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(name) || name.Length > 253 || !name.Contains('.') || System.Net.IPAddress.TryParse(name, out _) || Differs(name, machineName))
            return null;
        return name.Split('.').All(l => l.Length is > 0 and <= 63 && l.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && l[0] != '-' && l[^1] != '-') ? name : null;
    }

    private static string Label(string name)
    {
        var label = name.Split('.')[0];
        return label.Length > NetbiosNameLength ? label[..NetbiosNameLength] : label;
    }
}
