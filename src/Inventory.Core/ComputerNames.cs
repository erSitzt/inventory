namespace Inventory.Core;

public static class ComputerNames
{
    private static readonly char[] Separators = ['\r', '\n', ',', ';', '\t', ' '];

    /// <summary>Zerlegt frei eingegebenen Text (eine Zeile je Rechner, auch Komma/Semikolon) in eindeutige Rechnernamen.</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.TrimStart('\\').TrimEnd('$');
            if (IsValid(name) && seen.Add(name))
                result.Add(name);
        }
        return result;
    }

    public static bool IsValid(string name) =>
        name.Length is > 0 and <= 253 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');
}
