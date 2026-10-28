using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Inventory.Data.Services;

/// <summary>Was über und unter der Tabelle eines PDF-Berichts steht, und die übersetzten Texte dafür.</summary>
/// <param name="Meta">Zeile unter dem Titel: Quelle, Sammlung, Zeitraum, Zeilen und Stand.</param>
/// <param name="Description">Absatz über der Tabelle auf der ersten Seite; höchstens drei Zeilen.</param>
/// <param name="Organization">Name der Organisation im Kopf, im Fuß und als Autor des Dokuments.</param>
/// <param name="TruncatedNote">Hinweis, wenn Zeilen weggelassen wurden; {0} = gezeigte, {1} = vorhandene Zeilen.</param>
/// <param name="ColumnsLabel">Fuß, wenn die Spalten auf mehrere Seitenfolgen verteilt sind; {0} = Teil, {1} = Teile.</param>
/// <summary>Ein Abschnitt eines mehrteiligen Berichts: Überschrift, Absätze, optional eine Tabelle.</summary>
/// <param name="Level">1 = Abschnitt, 2 = Unterabschnitt (kleinere Überschrift).</param>
/// <param name="TotalRow">Die letzte Tabellenzeile ist eine Summe: fett und mit Linie darüber.</param>
/// <param name="NewPage">Der Abschnitt beginnt auf einer neuen Seite.</param>
public sealed record ReportSection(string Heading, IReadOnlyList<string>? Paragraphs = null, ReportTable? Table = null, bool TotalRow = false, int Level = 1, bool NewPage = false);

public sealed record ReportPdfOptions(
    string Title,
    string Meta = "",
    string? Description = null,
    string? Organization = null,
    ReportLogo? Logo = null,
    string PageLabel = "Page {0} of {1}",
    string TruncatedNote = "First {0} of {1} rows.",
    string NoEntries = "No entries",
    string ColumnsLabel = "Columns {0}/{1}",
    string GeneratedBy = "Created with Inventory");

/// <summary>
/// Schreibt eine Berichtstabelle als PDF: A4 quer, Kopf mit Titel, Angaben und Logo, Tabelle mit farbiger Kopfzeile auf
/// jeder Seite, Fuß mit Seitenzahlen. Lange Werte brechen um (höchstens drei Zeilen). Passen nicht alle Spalten
/// nebeneinander, folgen sie auf weiteren Seiten – jeweils mit der ersten Spalte davor, damit jede Zeile zuzuordnen bleibt.
/// Bewusst ohne Bibliothek: mit den Standardschriften von PDF muss keine Schrift eingebettet werden.
/// </summary>
public static class ReportPdf
{
    public const string ContentType = "application/pdf";
    /// <summary>Mehr Zeilen kommen nicht ins PDF (das wären hunderte Seiten); der Bericht weist darauf hin.</summary>
    public const int MaxRows = 5000;

    private const double PageWidth = 841.89, PageHeight = 595.28, Margin = 36;
    private const double Usable = PageWidth - 2 * Margin;
    private const double FontSize = 8, LineHeight = 10, PadX = 4, PadY = 3.5, MaxCellLines = 3, MaxHeaderLines = 2;
    private const double TitleSize = 16, MetaSize = 8.5, DescriptionSize = 8.5, FooterSize = 7.5;
    private const double HeaderHeight = 46, LogoHeight = 30, LogoWidth = 140;
    private const double MinColumn = 30, MaxColumn = 220, SqueezeTo = 100;
    /// <summary>Oberkante des Seiteninhalts unter dem Kopf und Unterkante über dem Fuß.</summary>
    private const double ContentTop = PageHeight - Margin - HeaderHeight - 12, ContentBottom = Margin + 20;

    // Farben (RGB 0–1): Akzent wie die Oberfläche, Text fast schwarz, Nebensachen grau.
    private static readonly double[] Accent = [0.31, 0.275, 0.898];
    private static readonly double[] Ink = [0.067, 0.094, 0.153];
    private static readonly double[] Muted = [0.42, 0.447, 0.502];
    private static readonly double[] Stripe = [0.961, 0.969, 0.984];
    private static readonly double[] Rule = [0.898, 0.906, 0.922];
    private static readonly double[] White = [1, 1, 1];
    private static readonly double[] Warning = [0.706, 0.325, 0.035];

    private enum Font { Regular, Bold, Italic }

    /// <param name="subtitle">Zeile unter dem Titel, z. B. Quelle, Sammlung und Stand.</param>
    /// <param name="truncatedNote">Text für den Hinweis, wenn Zeilen weggelassen wurden; {0} = gezeigte, {1} = vorhandene Zeilen.</param>
    public static byte[] Write(ReportTable table, string title, string subtitle, string pageLabel = "Page {0} of {1}", string truncatedNote = "First {0} of {1} rows.") =>
        Write(table, new ReportPdfOptions(title, subtitle, PageLabel: pageLabel, TruncatedNote: truncatedNote));

    public static byte[] Write(ReportTable table, ReportPdfOptions options)
    {
        var rows = table.Rows.Count > MaxRows ? table.Rows.Take(MaxRows).ToList() : table.Rows;
        var note = table.Rows.Count > MaxRows ? string.Format(CultureInfo.InvariantCulture, options.TruncatedNote, MaxRows, table.Rows.Count) : null;
        List<string> description = string.IsNullOrWhiteSpace(options.Description) ? [] : Wrap(options.Description.Trim(), Usable, DescriptionSize, Font.Regular, 3);
        // Was auf der ersten Seite zwischen Kopf und Tabelle steht.
        var intro = (note is null ? 0 : 12) + description.Count * 11 + (note is null && description.Count == 0 ? 0 : 6);

        var widths = ColumnWidths(table, rows);
        var bands = Bands(widths).Select(band => Stretch(band, widths)).ToList();

        // Erst verteilen, dann zeichnen: „Seite x von y“ braucht die Zahl aller Seiten.
        var pages = new List<Page>();
        foreach (var (band, bandIndex) in bands.Select((b, n) => (b, n)))
        {
            var header = band.Columns.Select((c, n) => Wrap(table.Columns[c].Title, band.Widths[n] - 2 * PadX, FontSize, Font.Bold, (int)MaxHeaderLines)).ToList();
            var headerHeight = header.Max(l => (int?)l.Count).GetValueOrDefault(1) * LineHeight + 2 * PadY;
            var page = new Page(bandIndex, band, header, headerHeight, pages.Count == 0 ? intro : 0);
            var y = ContentTop - page.Intro - headerHeight;
            foreach (var row in rows)
            {
                var cells = band.Columns.Select((c, n) => c < row.Length && !string.IsNullOrEmpty(row[c])
                    ? Wrap(row[c]!, band.Widths[n] - 2 * PadX, FontSize, Font.Regular, (int)MaxCellLines)
                    : []).ToList();
                var height = Math.Max(1, cells.Max(l => (int?)l.Count).GetValueOrDefault(1)) * LineHeight + 2 * PadY;
                if (y - height < ContentBottom && page.Rows.Count > 0)
                {
                    pages.Add(page);
                    page = new Page(bandIndex, band, header, headerHeight, 0);
                    y = ContentTop - headerHeight;
                }
                page.Rows.Add((cells, height));
                y -= height;
            }
            pages.Add(page);
        }

        var contents = new List<string>();
        foreach (var (page, number) in pages.Select((p, n) => (p, n + 1)))
        {
            var content = new StringBuilder();
            Head(content, options);
            var y = ContentTop;
            if (page.Intro > 0)
            {
                if (note is not null)
                {
                    Text(content, Margin, y - 8, FontSize, Font.Bold, Warning, Fit(note, Usable, FontSize, bold: true));
                    y -= 12;
                }
                foreach (var line in description)
                {
                    Text(content, Margin, y - 8.5, DescriptionSize, Font.Regular, Muted, line);
                    y -= 11;
                }
                y -= 6;
            }
            y = Table(content, table, page, y);
            if (rows.Count == 0)
                Text(content, Margin + PadX, y - 13, FontSize, Font.Italic, Muted, options.NoEntries);
            Foot(content, options, number, pages.Count, bands.Count > 1 ? string.Format(CultureInfo.InvariantCulture, options.ColumnsLabel, page.Band + 1, bands.Count) : null);
            contents.Add(content.ToString());
        }
        return Assemble(contents, options);
    }

    /// <summary>
    /// Ein mehrteiliger Bericht: Abschnitte mit Überschrift, Absätzen und Tabellen fließen über die Seiten; Tabellen wiederholen
    /// ihre Kopfzeile nach einem Seitenumbruch. Alle Spalten einer Tabelle stehen nebeneinander (breite Spalten brechen um).
    /// </summary>
    public static byte[] WriteSections(IReadOnlyList<ReportSection> sections, ReportPdfOptions options)
    {
        var flow = new Flow(options);
        if (!string.IsNullOrWhiteSpace(options.Description))
        {
            foreach (var line in Wrap(options.Description.Trim(), Usable, DescriptionSize, Font.Regular, 6))
            {
                Text(flow.Page, Margin, flow.Y - 8.5, DescriptionSize, Font.Regular, Muted, line);
                flow.Y -= 11;
            }
            flow.Y -= 8;
        }
        foreach (var section in sections)
        {
            if (section.NewPage && flow.Y < ContentTop)
                flow.Start();
            var size = section.Level == 1 ? 12.5 : 9.5;
            // Eine Überschrift nicht allein unten auf der Seite stehen lassen.
            flow.Need(size + 8 + 3 * LineHeight);
            Text(flow.Page, Margin, flow.Y - size, size, Font.Bold, section.Level == 1 ? Accent : Ink, Fit(section.Heading, Usable, size, bold: true));
            flow.Y -= size + (section.Level == 1 ? 9 : 5);
            foreach (var paragraph in section.Paragraphs ?? [])
            {
                foreach (var line in Wrap(paragraph, Usable, DescriptionSize, Font.Regular, 12))
                {
                    flow.Need(11);
                    Text(flow.Page, Margin, flow.Y - 8.5, DescriptionSize, Font.Regular, Ink, line);
                    flow.Y -= 11;
                }
                flow.Y -= 3;
            }
            if (section.Table is { } table)
            {
                flow.Y -= 2;
                FlowTable(flow, table, section.TotalRow);
            }
            flow.Y -= section.Level == 1 ? 14 : 8;
        }
        var contents = flow.Pages.Select((p, n) =>
        {
            Foot(p, options, n + 1, flow.Pages.Count, null);
            return p.ToString();
        }).ToList();
        return Assemble(contents, options);
    }

    /// <summary>Seiten eines fließenden Berichts mit der aktuellen Schreibposition.</summary>
    private sealed class Flow
    {
        private readonly ReportPdfOptions _options;
        public List<StringBuilder> Pages { get; } = [];
        public StringBuilder Page { get; private set; } = null!;
        public double Y { get; set; }

        public Flow(ReportPdfOptions options)
        {
            _options = options;
            Start();
        }

        public void Start()
        {
            Page = new StringBuilder();
            Head(Page, _options);
            Pages.Add(Page);
            Y = ContentTop;
        }

        /// <summary>Beginnt eine neue Seite, wenn <paramref name="height"/> nicht mehr passt; true, wenn umgebrochen wurde.</summary>
        public bool Need(double height)
        {
            if (Y - height >= ContentBottom)
                return false;
            Start();
            return true;
        }
    }

    /// <summary>Zeichnet eine Tabelle an der Schreibposition und bricht bei Bedarf auf neue Seiten um, mit wiederholter Kopfzeile.</summary>
    private static void FlowTable(Flow flow, ReportTable table, bool totalRow)
    {
        var rows = table.Rows.Count > MaxRows ? table.Rows.Take(MaxRows).ToList() : table.Rows;
        var widths = ColumnWidths(table, rows);
        var factor = widths.Sum() is > 0 and var sum ? Usable / sum : 1;
        widths = widths.Select(w => w * factor).ToArray();
        var header = widths.Select((w, c) => Wrap(table.Columns[c].Title, w - 2 * PadX, FontSize, Font.Bold, (int)MaxHeaderLines)).ToList();
        var headerHeight = header.Max(l => (int?)l.Count).GetValueOrDefault(1) * LineHeight + 2 * PadY;

        void Header()
        {
            Rect(flow.Page, Margin, flow.Y - headerHeight, Usable, headerHeight, Accent);
            var x = Margin;
            for (var c = 0; c < widths.Length; c++)
            {
                Cell(flow.Page, x, flow.Y, widths[c], header[c], Font.Bold, White, table.Columns[c].Numeric);
                x += widths[c];
            }
            flow.Y -= headerHeight;
        }

        flow.Need(headerHeight + LineHeight + 2 * PadY);
        Header();
        if (rows.Count == 0)
        {
            Text(flow.Page, Margin + PadX, flow.Y - 13, FontSize, Font.Italic, Muted, "–");
            flow.Y -= 18;
            return;
        }
        foreach (var (row, index) in rows.Select((r, n) => (r, n)))
        {
            var total = totalRow && index == rows.Count - 1;
            var font = total ? Font.Bold : Font.Regular;
            var cells = widths.Select((w, c) => c < row.Length && !string.IsNullOrEmpty(row[c]) ? Wrap(row[c]!, w - 2 * PadX, FontSize, font, (int)MaxCellLines) : []).ToList();
            var height = Math.Max(1, cells.Max(l => (int?)l.Count).GetValueOrDefault(1)) * LineHeight + 2 * PadY;
            if (flow.Need(height))
                Header();
            if (total)
                Line(flow.Page, Margin, flow.Y, Margin + Usable, flow.Y, 1, Ink);
            else if (index % 2 == 1)
                Rect(flow.Page, Margin, flow.Y - height, Usable, height, Stripe);
            var x = Margin;
            for (var c = 0; c < widths.Length; c++)
            {
                Cell(flow.Page, x, flow.Y, widths[c], cells[c], font, Ink, table.Columns[c].Numeric);
                x += widths[c];
            }
            flow.Y -= height;
            if (!total)
                Line(flow.Page, Margin, flow.Y, Margin + Usable, flow.Y, 0.4, Rule);
        }
    }

    private sealed record Band(List<int> Columns, double[] Widths);

    private sealed record Page(int Band, Band Layout, List<List<string>> Header, double HeaderHeight, double Intro)
    {
        public List<(List<List<string>> Cells, double Height)> Rows { get; } = [];
    }

    /// <summary>Titel und Angaben links, Logo und Organisation rechts, darunter eine Linie in der Akzentfarbe.</summary>
    private static void Head(StringBuilder content, ReportPdfOptions options)
    {
        var top = PageHeight - Margin;
        var right = 0.0;
        if (options.Logo is { } logo)
        {
            var scale = Math.Min(LogoHeight / logo.Height, LogoWidth / logo.Width);
            double w = logo.Width * scale, h = logo.Height * scale;
            content.Append("q ").Append(N(w)).Append(" 0 0 ").Append(N(h)).Append(' ').Append(N(PageWidth - Margin - w)).Append(' ').Append(N(top - h)).Append(" cm /Im1 Do Q\n");
            right = w;
            if (!string.IsNullOrWhiteSpace(options.Organization))
            {
                var name = Fit(options.Organization.Trim(), LogoWidth + 60, 8, bold: true);
                Text(content, PageWidth - Margin - Width(name, 8, true), top - HeaderHeight + 5, 8, Font.Bold, Muted, name);
                right = Math.Max(right, Width(name, 8, true));
            }
        }
        else if (!string.IsNullOrWhiteSpace(options.Organization))
        {
            var name = Fit(options.Organization.Trim(), Usable / 3, 10, bold: true);
            right = Width(name, 10, true);
            Text(content, PageWidth - Margin - right, top - 14, 10, Font.Bold, Ink, name);
        }
        var available = Usable - (right > 0 ? right + 24 : 0);
        Text(content, Margin, top - 16, TitleSize, Font.Bold, Ink, Fit(options.Title, available, TitleSize, bold: true));
        Text(content, Margin, top - 32, MetaSize, Font.Regular, Muted, Fit(options.Meta, available, MetaSize, bold: false));
        Line(content, Margin, top - HeaderHeight, PageWidth - Margin, top - HeaderHeight, 1.5, Accent);
    }

    /// <summary>Kopfzeile in der Akzentfarbe, Zeilen im Wechsel hinterlegt und durch feine Linien getrennt; liefert die Unterkante.</summary>
    private static double Table(StringBuilder content, ReportTable table, Page page, double top)
    {
        var band = page.Layout;
        var width = band.Widths.Sum();
        if (band.Columns.Count == 0)
            return top;
        Rect(content, Margin, top - page.HeaderHeight, width, page.HeaderHeight, Accent);
        var x = Margin;
        foreach (var (column, n) in band.Columns.Select((c, n) => (c, n)))
        {
            Cell(content, x, top, band.Widths[n], page.Header[n], Font.Bold, White, table.Columns[column].Numeric);
            x += band.Widths[n];
        }
        var y = top - page.HeaderHeight;
        foreach (var ((cells, height), index) in page.Rows.Select((r, n) => (r, n)))
        {
            if (index % 2 == 1)
                Rect(content, Margin, y - height, width, height, Stripe);
            x = Margin;
            foreach (var (column, n) in band.Columns.Select((c, n) => (c, n)))
            {
                Cell(content, x, y, band.Widths[n], cells[n], Font.Regular, Ink, table.Columns[column].Numeric);
                x += band.Widths[n];
            }
            y -= height;
            Line(content, Margin, y, Margin + width, y, 0.4, Rule);
        }
        return y;
    }

    private static void Foot(StringBuilder content, ReportPdfOptions options, int page, int pages, string? columns)
    {
        Line(content, Margin, Margin + 11, PageWidth - Margin, Margin + 11, 0.5, Rule);
        var label = string.Format(CultureInfo.InvariantCulture, options.PageLabel, page, pages);
        if (columns is not null)
            label = columns + "  ·  " + label;
        Text(content, PageWidth - Margin - Width(label, FooterSize, false), Margin, FooterSize, Font.Regular, Muted, label);
        Text(content, (PageWidth - Width(options.GeneratedBy, FooterSize, false)) / 2, Margin, FooterSize, Font.Regular, Muted, options.GeneratedBy);
        var left = string.IsNullOrWhiteSpace(options.Organization) ? options.Title : options.Organization.Trim() + "  ·  " + options.Title;
        Text(content, Margin, Margin, FooterSize, Font.Regular, Muted, Fit(left, Usable / 2 - Width(options.GeneratedBy, FooterSize, false) / 2 - 12, FooterSize, bold: false));
    }

    /// <summary>
    /// Natürliche Breite jeder Spalte (Überschrift und die ersten Zeilen), in Grenzen. Eine Überschrift darf auf zwei Zeilen
    /// umbrechen, aber kein Wort darin. Passt die Tabelle nicht auf die Seite, werden breite Spalten zuerst schmaler –
    /// sie brechen dann um –, bevor Spalten auf weitere Seiten wandern.
    /// </summary>
    private static double[] ColumnWidths(ReportTable table, IReadOnlyList<string?[]> rows)
    {
        var widths = new double[table.Columns.Count];
        for (var i = 0; i < widths.Length; i++)
        {
            var title = table.Columns[i].Title;
            var longestWord = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => Width(w, FontSize, bold: true)).DefaultIfEmpty(0).Max();
            var natural = Math.Max(longestWord, Width(title, FontSize, bold: true) / MaxHeaderLines);
            foreach (var row in rows.Take(300))
            {
                if (i < row.Length && row[i] is { Length: > 0 } value)
                    natural = Math.Max(natural, Width(OneLine(value), FontSize, bold: false));
            }
            widths[i] = Math.Clamp(natural + 2 * PadX + 1, Math.Max(MinColumn, longestWord + 2 * PadX + 1), Math.Max(MaxColumn, longestWord + 2 * PadX + 1));
        }
        var excess = widths.Sum() - Usable;
        var shrinkable = widths.Sum(w => Math.Max(0, w - SqueezeTo));
        if (excess > 0 && shrinkable > 0)
        {
            var factor = Math.Min(1, excess / shrinkable);
            for (var i = 0; i < widths.Length; i++)
                widths[i] -= Math.Max(0, widths[i] - SqueezeTo) * factor;
        }
        return widths;
    }

    /// <summary>Jede Seitenfolge nutzt die ganze Breite, bündig mit Kopf- und Fußlinie: der freie Platz geht anteilig an die Spalten.</summary>
    private static Band Stretch(List<int> columns, double[] widths)
    {
        var natural = columns.Select(c => widths[c]).ToArray();
        var total = natural.Sum();
        var factor = total > 0 ? Math.Max(1, Usable / total) : 1;
        return new Band(columns, natural.Select(w => w * factor).ToArray());
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

    private static void Cell(StringBuilder content, double x, double top, double width, List<string> lines, Font font, double[] color, bool right)
    {
        var baseline = top - PadY - 7.4;
        foreach (var line in lines)
        {
            var left = right ? x + width - PadX - Width(line, FontSize, font == Font.Bold) : x + PadX;
            Text(content, left, baseline, FontSize, font, color, line);
            baseline -= LineHeight;
        }
    }

    private static void Text(StringBuilder content, double x, double y, double size, Font font, double[] color, string text)
    {
        if (text.Length == 0)
            return;
        content.Append(Rgb(color)).Append(" rg BT /").Append(font switch { Font.Bold => "F2", Font.Italic => "F3", _ => "F1" }).Append(' ').Append(N(size))
            .Append(" Tf ").Append(N(x)).Append(' ').Append(N(y)).Append(" Td <");
        foreach (var b in Encode(text))
            content.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        content.Append("> Tj ET\n");
    }

    private static void Rect(StringBuilder content, double x, double y, double width, double height, double[] color) =>
        content.Append(Rgb(color)).Append(" rg ").Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append(" re f\n");

    private static void Line(StringBuilder content, double x1, double y1, double x2, double y2, double width, double[] color) =>
        content.Append(Rgb(color)).Append(" RG ").Append(N(width)).Append(" w ").Append(N(x1)).Append(' ').Append(N(y1)).Append(" m ")
            .Append(N(x2)).Append(' ').Append(N(y2)).Append(" l S\n");

    private static string Rgb(double[] color) => string.Join(' ', color.Select(c => c.ToString("0.###", CultureInfo.InvariantCulture)));

    private static string OneLine(string value) => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    /// <summary>
    /// Bricht an Leerzeichen um, ein zu langes Wort hart. Mehr als <paramref name="maxLines"/> Zeilen werden es nicht:
    /// die letzte endet dann mit Auslassungszeichen.
    /// </summary>
    private static List<string> Wrap(string text, double width, double size, Font font, int maxLines)
    {
        var bold = font == Font.Bold;
        var lines = new List<string>();
        var current = "";
        foreach (var token in OneLine(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = token;
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Width(candidate, size, bold) <= width + 0.5)
            {
                current = candidate;
                continue;
            }
            if (current.Length > 0)
                lines.Add(current);
            while (word.Length > 1 && Width(word, size, bold) > width + 0.5)
            {
                var cut = Prefix(word, width, size, bold);
                lines.Add(word[..cut]);
                word = word[cut..];
            }
            current = word;
        }
        if (current.Length > 0)
            lines.Add(current);
        if (lines.Count <= maxLines)
            return lines;
        // Die letzte Zeile mit allem Übrigen: Fit kürzt sie und setzt die Auslassungspunkte.
        return [.. lines.Take(maxLines - 1), Fit(string.Join(' ', lines.Skip(maxLines - 1)), width, size, bold)];
    }

    internal static List<string> Wrap(string text, double width, double size, bool bold, int maxLines) =>
        Wrap(text, width, size, bold ? Font.Bold : Font.Regular, maxLines);

    /// <summary>Wie viele Zeichen vom Anfang in die Breite passen, mindestens eines.</summary>
    private static int Prefix(string word, double width, double size, bool bold)
    {
        var used = 0.0;
        for (var i = 0; i < word.Length; i++)
        {
            used += CharWidth(word[i], bold) * size / 1000;
            if (used > width + 0.5)
                return Math.Max(1, i);
        }
        return word.Length;
    }

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
        // Ein Zeichen außerhalb der Grundebene (Emoji) wird zu einem Fragezeichen: die erste Hälfte trägt dessen Breite, die zweite keine.
        if (char.IsLowSurrogate(c))
            return 0;
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
        var bytes = new List<byte>(text.Length);
        foreach (var c in text)
        {
            // Ein Zeichen außerhalb der Grundebene (Emoji) besteht aus zwei Hälften; es wird zu einem einzigen Fragezeichen.
            if (char.IsLowSurrogate(c))
                continue;
            bytes.Add(c switch
            {
                >= ' ' and <= '~' => (byte)c,
                >= '\u00A0' and <= '\u00FF' => (byte)c,
                '€' => 0x80, '‚' => 0x82, '„' => 0x84, '…' => 0x85, '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94, '•' => 0x95, '–' => 0x96, '—' => 0x97, '™' => 0x99,
                'Š' => 0x8A, 'š' => 0x9A, 'Ž' => 0x8E, 'ž' => 0x9E, 'Œ' => 0x8C, 'œ' => 0x9C, 'Ÿ' => 0x9F,
                _ => (byte)'?',
            });
        }
        return [.. bytes];
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Setzt die Seiten zu einer PDF-Datei zusammen: Katalog, Seitenbaum, drei Schriften, Dokumentinfo, das Logo (mit
    /// Deckkraftmaske) einmal für alle Seiten, je Seite ein komprimierter Inhalt, Querverweistabelle.
    /// </summary>
    private static byte[] Assemble(List<string> pages, ReportPdfOptions options)
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
        void Stream(string dictionary, byte[] data)
        {
            Raw($"<< {dictionary} /Length {data.Length} >>\nstream\n");
            output.Write(data);
            Raw("\nendstream\nendobj\n");
        }
        string Hex(string text) => $"<{Convert.ToHexString(Encode(text))}>";

        // 1 Katalog, 2 Seitenbaum, 3 bis 5 Schriften, 6 Dokumentinfo, dann Logo und Maske, danach je Seite zwei Objekte.
        var logo = options.Logo;
        var image = logo is null ? 0 : 7;
        var mask = logo?.Alpha is null ? 0 : 8;
        var first = 7 + (logo is null ? 0 : 1) + (mask == 0 ? 0 : 1);

        Raw("%PDF-1.4\n%âãÏÓ\n");
        Begin(1);
        Raw("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2);
        Raw($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(' ', Enumerable.Range(0, pages.Count).Select(i => $"{first + 2 * i} 0 R"))}] >>\nendobj\n");
        foreach (var (font, number) in new[] { ("Helvetica", 3), ("Helvetica-Bold", 4), ("Helvetica-Oblique", 5) })
        {
            Begin(number);
            Raw($"<< /Type /Font /Subtype /Type1 /BaseFont /{font} /Encoding /WinAnsiEncoding >>\nendobj\n");
        }
        Begin(6);
        var info = $"/Title {Hex(options.Title)} /Producer (Inventory) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z)";
        if (!string.IsNullOrWhiteSpace(options.Organization))
            info += $" /Author {Hex(options.Organization.Trim())}";
        if (!string.IsNullOrWhiteSpace(options.Description))
            info += $" /Subject {Hex(options.Description.Trim())}";
        Raw($"<< {info} >>\nendobj\n");
        if (logo is not null)
        {
            Begin(image);
            Stream($"/Type /XObject /Subtype /Image /Width {logo.Width} /Height {logo.Height} /ColorSpace /{logo.ColorSpace} /BitsPerComponent 8 /Filter /{logo.Filter}"
                + (mask == 0 ? "" : $" /SMask {mask} 0 R"), logo.Data);
            if (mask != 0)
            {
                Begin(mask);
                Stream($"/Type /XObject /Subtype /Image /Width {logo.Width} /Height {logo.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", logo.Alpha!);
            }
        }
        var resources = "/Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >>" + (logo is null ? "" : $" /XObject << /Im1 {image} 0 R >>");
        for (var i = 0; i < pages.Count; i++)
        {
            Begin(first + 2 * i);
            Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << {resources} >> /Contents {first + 2 * i + 1} 0 R >>\nendobj\n");
            Begin(first + 2 * i + 1);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(Encoding.Latin1.GetBytes(pages[i]));
            Stream("/Filter /FlateDecode", compressed.ToArray());
        }
        var xref = output.Position;
        Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Raw($"{offset:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
