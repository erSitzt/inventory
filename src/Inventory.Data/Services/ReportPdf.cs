using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Inventory.Data.Services;

/// <summary>
/// Schreibt eine Berichtstabelle als PDF: A4 quer, Kopfzeile auf jeder Seite, Seitenzahlen. Passen nicht alle Spalten
/// nebeneinander, folgen sie auf weiteren Seiten – jeweils mit der ersten Spalte davor, damit jede Zeile zuzuordnen bleibt.
/// Bewusst ohne Bibliothek: mit den Standardschriften von PDF muss keine Schrift eingebettet werden.
/// </summary>
public static class ReportPdf
{
    public const string ContentType = "application/pdf";
    /// <summary>Mehr Zeilen kommen nicht ins PDF (das wären hunderte Seiten); der Bericht weist darauf hin.</summary>
    public const int MaxRows = 5000;

    private const double PageWidth = 841.89, PageHeight = 595.28, Margin = 28;
    private const double FontSize = 7.5, RowHeight = 11, Padding = 3;
    private const double TitleSize = 13, SubtitleSize = 8.5, FooterSize = 7;
    private const double MinColumn = 26, MaxColumn = 190;
    private const double Usable = PageWidth - 2 * Margin;

    /// <param name="subtitle">Zeile unter dem Titel, z. B. Quelle, Sammlung und Stand.</param>
    /// <param name="truncatedNote">Text für den Hinweis, wenn Zeilen weggelassen wurden; {0} = gezeigte, {1} = vorhandene Zeilen.</param>
    public static byte[] Write(ReportTable table, string title, string subtitle, string pageLabel = "Page {0} of {1}", string truncatedNote = "First {0} of {1} rows.")
    {
        var rows = table.Rows.Count > MaxRows ? table.Rows.Take(MaxRows).ToList() : table.Rows;
        if (table.Rows.Count > MaxRows)
            subtitle += "  ·  " + string.Format(CultureInfo.InvariantCulture, truncatedNote, MaxRows, table.Rows.Count);
        var widths = ColumnWidths(table, rows);
        var bands = Bands(widths);

        // Höhe: Titel und Untertitel oben, Fußzeile unten, dazwischen Kopfzeile und Zeilen.
        var top = PageHeight - Margin - TitleSize - 6 - SubtitleSize - 8;
        var perPage = Math.Max(1, (int)((top - Margin - FooterSize - 6) / RowHeight) - 1);
        var pages = new List<string>();
        var pageCount = bands.Count * Math.Max(1, (int)Math.Ceiling(rows.Count / (double)perPage));
        foreach (var band in bands)
        {
            for (var start = 0; start == 0 || start < rows.Count; start += perPage)
            {
                var content = new StringBuilder();
                Text(content, Margin, PageHeight - Margin - TitleSize, TitleSize, bold: true, Fit(title, Usable, TitleSize, bold: true));
                Text(content, Margin, PageHeight - Margin - TitleSize - 6 - SubtitleSize, SubtitleSize, bold: false, Fit(subtitle, Usable, SubtitleSize, bold: false));

                // Kopfzeile: grau hinterlegt, fett.
                var bandWidth = band.Sum(i => widths[i]);
                content.Append("0.9 g ").Append(N(Margin)).Append(' ').Append(N(top - RowHeight)).Append(' ').Append(N(bandWidth)).Append(' ').Append(N(RowHeight)).Append(" re f 0 g\n");
                var x = Margin;
                foreach (var i in band)
                {
                    Cell(content, x, top - RowHeight, widths[i], table.Columns[i].Title, bold: true, right: table.Columns[i].Numeric);
                    x += widths[i];
                }

                var y = top - RowHeight;
                foreach (var (row, index) in rows.Skip(start).Take(perPage).Select((r, n) => (r, n)))
                {
                    y -= RowHeight;
                    if (index % 2 == 1)
                        content.Append("0.96 g ").Append(N(Margin)).Append(' ').Append(N(y)).Append(' ').Append(N(bandWidth)).Append(' ').Append(N(RowHeight)).Append(" re f 0 g\n");
                    x = Margin;
                    foreach (var i in band)
                    {
                        if (i < row.Length && !string.IsNullOrEmpty(row[i]))
                            Cell(content, x, y, widths[i], row[i]!, bold: false, right: table.Columns[i].Numeric);
                        x += widths[i];
                    }
                }
                if (rows.Count == 0)
                    Text(content, Margin, top - 2 * RowHeight - 4, FontSize, bold: false, "–");

                var footer = string.Format(CultureInfo.InvariantCulture, pageLabel, pages.Count + 1, pageCount);
                Text(content, PageWidth - Margin - Width(footer, FooterSize, false), Margin, FooterSize, bold: false, footer);
                Text(content, Margin, Margin, FooterSize, bold: false, Fit(title, Usable - 120, FooterSize, bold: false));
                pages.Add(content.ToString());
            }
        }
        return Assemble(pages, title);
    }

    /// <summary>Natürliche Breite jeder Spalte (Überschrift und die ersten Zeilen), in Grenzen.</summary>
    private static double[] ColumnWidths(ReportTable table, IReadOnlyList<string?[]> rows)
    {
        var widths = new double[table.Columns.Count];
        for (var i = 0; i < widths.Length; i++)
        {
            var natural = Width(table.Columns[i].Title, FontSize, bold: true);
            foreach (var row in rows.Take(300))
            {
                if (i < row.Length && row[i] is { Length: > 0 } value)
                    natural = Math.Max(natural, Width(value, FontSize, bold: false));
            }
            widths[i] = Math.Clamp(natural + 2 * Padding, MinColumn, MaxColumn);
        }
        // Passt alles auf eine Seite, bekommt die Tabelle die ganze Breite.
        var total = widths.Sum();
        if (total > 0 && total < Usable)
        {
            var factor = Math.Min(Usable / total, 1.6);
            for (var i = 0; i < widths.Length; i++)
                widths[i] *= factor;
        }
        return widths;
    }

    /// <summary>Verteilt die Spalten auf Seitenfolgen; ab der zweiten steht die erste Spalte jeweils vorn.</summary>
    internal static List<List<int>> Bands(double[] widths)
    {
        var bands = new List<List<int>>();
        var current = new List<int>();
        var used = 0.0;
        for (var i = 0; i < widths.Length; i++)
        {
            if (current.Count > (bands.Count == 0 ? 0 : 1) && used + widths[i] > Usable + 0.01)
            {
                bands.Add(current);
                current = [0];
                used = widths[0];
            }
            current.Add(i);
            used += widths[i];
        }
        if (current.Count > 0 || bands.Count == 0)
            bands.Add(current);
        return bands;
    }

    private static void Cell(StringBuilder content, double x, double y, double width, string value, bool bold, bool right)
    {
        var text = Fit(OneLine(value), width - 2 * Padding, FontSize, bold);
        var left = right ? x + width - Padding - Width(text, FontSize, bold) : x + Padding;
        Text(content, left, y + 3.2, FontSize, bold, text);
    }

    private static void Text(StringBuilder content, double x, double y, double size, bool bold, string text)
    {
        if (text.Length == 0)
            return;
        content.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(N(size)).Append(" Tf ").Append(N(x)).Append(' ').Append(N(y)).Append(" Td <");
        foreach (var b in Encode(text))
            content.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        content.Append("> Tj ET\n");
    }

    private static string OneLine(string value) => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    /// <summary>Kürzt mit Auslassungszeichen, bis der Text in die Breite passt.</summary>
    internal static string Fit(string text, double width, double size, bool bold)
    {
        // Ein halber Punkt Spielraum: die Spaltenbreite stammt aus derselben Messung und darf nicht an der Rundung scheitern.
        if (Width(text, size, bold) <= width + 0.5)
            return text;
        const string ellipsis = "…";
        var available = width - Width(ellipsis, size, bold);
        var length = text.Length;
        var used = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            used += CharWidth(text[i], bold) * size / 1000;
            if (used > available)
            {
                length = i;
                break;
            }
        }
        return text[..length].TrimEnd() + ellipsis;
    }

    internal static double Width(string text, double size, bool bold)
    {
        var units = 0;
        foreach (var c in text)
            units += CharWidth(c, bold);
        return units * size / 1000;
    }

    // Laufweiten von Helvetica für die Zeichen 32 bis 126 (in Tausendstel der Schriftgröße).
    private static readonly int[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    // Dasselbe für Helvetica-Bold.
    private static readonly int[] Bold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    private static int CharWidth(char c, bool bold)
    {
        var table = bold ? Bold : Regular;
        if (c is >= ' ' and <= '~')
            return table[c - ' '];
        // Buchstaben mit Akzent laufen so breit wie ihr Grundbuchstabe; alles Übrige wird großzügig geschätzt, damit nichts überläuft.
        // Hälften eines Zeichens außerhalb der Grundebene (Emoji) lassen sich nicht einzeln zerlegen; sie werden ohnehin zum Fragezeichen.
        if (char.IsSurrogate(c))
            return table['?' - ' '];
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.Length > 1 && decomposed[0] is >= ' ' and <= '~')
            return table[decomposed[0] - ' '];
        return c switch
        {
            'ß' => bold ? 611 : 611,
            '…' or '—' => 1000,
            '–' or '€' => 556,
            _ => bold ? 722 : 667,
        };
    }

    /// <summary>Der Zeichenvorrat der Standardschriften (WinAnsi); was darin fehlt, wird zum Fragezeichen.</summary>
    internal static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            bytes[i] = c switch
            {
                >= ' ' and <= '~' => (byte)c,
                >= ' ' and <= 'ÿ' => (byte)c,
                '€' => 0x80, '‚' => 0x82, '„' => 0x84, '…' => 0x85, '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94, '•' => 0x95, '–' => 0x96, '—' => 0x97, '™' => 0x99,
                'Š' => 0x8A, 'š' => 0x9A, 'Ž' => 0x8E, 'ž' => 0x9E, 'Œ' => 0x8C, 'œ' => 0x9C, 'Ÿ' => 0x9F,
                _ => (byte)'?',
            };
        }
        return bytes;
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Setzt die Seiten zu einer PDF-Datei zusammen: Katalog, Seitenbaum, zwei Schriften, je Seite ein komprimierter Inhalt, Querverweistabelle.</summary>
    private static byte[] Assemble(List<string> pages, string title)
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        void Begin(int number)
        {
            while (offsets.Count < number)
                offsets.Add(0);
            offsets[number - 1] = output.Position;
            Raw($"{number} 0 obj\n");
        }

        // 1 Katalog, 2 Seitenbaum, 3 und 4 Schriften, 5 Dokumentinfo, danach je Seite zwei Objekte (Seite, Inhalt).
        Raw("%PDF-1.4\n%âãÏÓ\n");
        Begin(1);
        Raw("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2);
        Raw($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(' ', Enumerable.Range(0, pages.Count).Select(i => $"{6 + 2 * i} 0 R"))}] >>\nendobj\n");
        Begin(3);
        Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Begin(4);
        Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        Begin(5);
        Raw($"<< /Title <{Convert.ToHexString(Encode(title))}> /Producer (Inventory) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>\nendobj\n");
        for (var i = 0; i < pages.Count; i++)
        {
            Begin(6 + 2 * i);
            Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {7 + 2 * i} 0 R >>\nendobj\n");
            Begin(7 + 2 * i);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(Encoding.Latin1.GetBytes(pages[i]));
            Raw($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            compressed.Position = 0;
            compressed.CopyTo(output);
            Raw("\nendstream\nendobj\n");
        }
        var xref = output.Position;
        Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Raw($"{offset:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
