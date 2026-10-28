using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Inventory.Data.Services;

/// <summary>Bringt eine Berichtstabelle in ein Dateiformat.</summary>
public static class ReportExport
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Semikolon-getrennt, wie es ein deutsches Excel beim Doppelklick erwartet.</summary>
    public static string Csv(ReportTable table)
    {
        var csv = new StringBuilder();
        Row(csv, table.Columns.Select(c => (string?)c.Title).ToArray());
        var numeric = table.Columns.Select(c => c.Numeric).ToArray();
        foreach (var row in table.Rows)
            Row(csv, numeric, row);
        return csv.ToString();
    }

    /// <summary>UTF-8 mit BOM, damit Excel die Umlaute erkennt.</summary>
    public static byte[] CsvBytes(ReportTable table) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Csv(table))];

    internal static void Row(StringBuilder csv, params string?[] values) => Row(csv, null, values);

    /// <param name="numeric">Zahlenspalten: eine echte Zahl wie -72 bleibt, wie sie ist, statt als mögliche Formel maskiert zu werden.</param>
    private static void Row(StringBuilder csv, bool[]? numeric, string?[] values)
    {
        csv.AppendJoin(';', values.Select((v, i) => Escape(v, numeric is not null && i < numeric.Length && numeric[i]))).Append("\r\n");

        static string Escape(string? value, bool number)
        {
            value ??= "";
            // Ein führendes =, +, - oder @ würde Excel als Formel ausführen.
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
                && !(number && decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _)))
                value = "'" + value;
            return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }
    }

    /// <summary>
    /// Eine Excel-Arbeitsmappe mit einem Blatt: fette, fixierte Kopfzeile mit Filter, Zahlenspalten als Zahlen, alles andere
    /// als Text (Formeln werden damit nie ausgeführt). Bewusst ohne Bibliothek: das Format ist ein ZIP mit wenigen XML-Dateien.
    /// </summary>
    public static byte[] Xlsx(ReportTable table, string sheetName)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Add(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Xml(SheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            // Format 1 = fette Schrift für die Kopfzeile.
            Add(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>
                """);
            using var sheet = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
            WriteSheet(sheet, table);
        }
        return buffer.ToArray();
    }

    private static void WriteSheet(TextWriter xml, ReportTable table)
    {
        var columns = table.Columns.Count;
        var lastCell = ColumnName(Math.Max(columns, 1) - 1) + (table.Rows.Count + 1).ToString(CultureInfo.InvariantCulture);
        xml.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + "\n");
        xml.Write("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        xml.Write($"""<dimension ref="A1:{lastCell}"/>""");
        xml.Write("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        if (columns > 0)
        {
            xml.Write("<cols>");
            for (var i = 0; i < columns; i++)
            {
                // Breite nach dem längsten Wert der ersten Zeilen, in Grenzen: lange Notizen sollen das Blatt nicht sprengen.
                var longest = table.Rows.Take(200).Select(r => r[i]?.Length ?? 0).Append(table.Columns[i].Title.Length).Max();
                var width = Math.Clamp(longest + 2, 8, 60).ToString(CultureInfo.InvariantCulture);
                var index = (i + 1).ToString(CultureInfo.InvariantCulture);
                xml.Write($"""<col min="{index}" max="{index}" width="{width}" customWidth="1"/>""");
            }
            xml.Write("</cols>");
        }
        xml.Write("<sheetData>");
        xml.Write("""<row r="1">""");
        for (var i = 0; i < columns; i++)
            Text(xml, ColumnName(i) + "1", table.Columns[i].Title, style: 1);
        xml.Write("</row>");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var number = (r + 2).ToString(CultureInfo.InvariantCulture);
            xml.Write($"""<row r="{number}">""");
            var row = table.Rows[r];
            for (var i = 0; i < columns && i < row.Length; i++)
            {
                var value = row[i];
                if (string.IsNullOrEmpty(value))
                    continue;
                var cell = ColumnName(i) + number;
                if (table.Columns[i].Numeric && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                    xml.Write($"""<c r="{cell}"><v>{parsed.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                else
                    Text(xml, cell, value, style: 0);
            }
            xml.Write("</row>");
        }
        xml.Write("</sheetData>");
        if (columns > 0)
            xml.Write($"""<autoFilter ref="A1:{lastCell}"/>""");
        xml.Write("</worksheet>");
    }

    private static void Text(TextWriter xml, string cell, string value, int style)
    {
        // Excel fasst höchstens 32767 Zeichen je Zelle.
        if (value.Length > 32767)
            value = value[..32767];
        xml.Write($"""<c r="{cell}"{(style == 0 ? "" : $""" s="{style}" """.TrimEnd())} t="inlineStr"><is><t xml:space="preserve">{Xml(value)}</t></is></c>""");
    }

    /// <summary>A, B, …, Z, AA, AB, …</summary>
    internal static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>Blattnamen: höchstens 31 Zeichen, ohne die Zeichen, die Excel dort verbietet.</summary>
    internal static string SheetName(string name)
    {
        // Erst die Zeichen entfernen, die XML nicht kennt: sonst bliebe von einem Namen aus Steuerzeichen ein leerer Blattname.
        var cleaned = new string(Valid(name).Select(c => c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? ' ' : c).ToArray()).Trim().Trim('\'');
        if (cleaned.Length > 31)
            cleaned = cleaned[..31].TrimEnd();
        return cleaned.Length == 0 ? "Report" : cleaned;
    }

    /// <summary>Maskiert für XML und entfernt Zeichen, die in XML 1.0 nicht vorkommen dürfen (Steuerzeichen aus Scan-Daten).</summary>
    private static string Xml(string value) => SecurityElement.Escape(Valid(value)) ?? "";

    private static string Valid(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
                continue;
            }
            if (c is '\t' or '\n' or '\r' || (c >= ' ' && c <= '\uD7FF') || (c >= '\uE000' && c <= '\uFFFD'))
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
