using System.Globalization;
using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="Updated">Rechner, deren Angaben übernommen wurden.</param>
/// <param name="UnknownNames">Namen aus der Tabelle, zu denen es keinen Rechner gibt.</param>
/// <param name="Errors">Zeilen, die wegen eines unlesbaren Werts ausgelassen wurden.</param>
public sealed record AssetImportResult(List<string> Updated, List<string> UnknownNames, List<string> Errors);

/// <summary>Liest die Tabelle für den Import von Bestandsangaben.</summary>
public static class AssetCsv
{
    /// <summary>Englische Spaltennamen; zugleich die Schlüssel der Übersetzung, unter der der Export sie schreibt.</summary>
    public static readonly string[] Columns = ["Name", "Owner", "Location", "Cost center", "Inventory number", "Purchase date", "Warranty until", "Tags", "Notes", "Description"];

    /// <summary>Sprachen, in denen der Export seine Spalten überschreibt.</summary>
    private static readonly CultureInfo[] ExportCultures = [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("de")];

    /// <summary>
    /// Ordnet eine Überschrift ihrer Spalte zu: englisch oder in einer der Sprachen des Exports, unabhängig von der Sprache
    /// des Aufrufers. null = unbekannte Spalte, wird ignoriert.
    /// </summary>
    public static string? Column(string header, IStringLocalizer<SharedResource> localizer)
    {
        var text = header.Trim().TrimStart('\uFEFF');
        if (Columns.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase)) is { } direct)
            return direct;
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in ExportCultures)
            {
                CultureInfo.CurrentUICulture = culture;
                if (Columns.FirstOrDefault(c => string.Equals(localizer[c].Value, text, StringComparison.OrdinalIgnoreCase)) is { } translated)
                    return translated;
            }
            return null;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>Der Export stellt Werten, die mit =, +, - oder @ beginnen, ein Hochkomma voran, damit Excel sie nicht als Formel ausführt.</summary>
    public static string Unescape(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && cell[1] is '=' or '+' or '-' or '@' ? cell[1..] : cell;

    public static DateOnly? Date(string text) =>
        DateOnly.TryParseExact(text, ["yyyy-MM-dd", "d.M.yyyy", "dd.MM.yyyy", "M/d/yyyy", "yyyy-MM-dd HH:mm", "dd.MM.yyyy HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>Zeilen und Zellen; das Trennzeichen (Semikolon, Komma oder Tabulator) ergibt sich aus der Kopfzeile, Anführungszeichen werden beachtet.</summary>
    public static List<List<string>> Parse(string csv)
    {
        var firstLine = csv.Split('\n')[0];
        var separator = new[] { ';', '\t', ',' }.OrderByDescending(s => firstLine.Count(ch => ch == s)).First();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                    quoted = false;
                else
                    cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0)
                quoted = true;
            else if (ch == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(c => c.Trim().Length > 0))
                    rows.Add(row);
                row = [];
            }
            else if (ch != '\r')
                cell.Append(ch);
        }
        row.Add(cell.ToString());
        if (row.Any(c => c.Trim().Length > 0))
            rows.Add(row);
        return rows;
    }
}
