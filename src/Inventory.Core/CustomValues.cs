using System.Globalization;
using System.Text.Json;
using Inventory.Core.Abstractions;

namespace Inventory.Core;

/// <summary>Werte eigener Inventarfelder: nach Typ normalisieren und die Ausgabe von Inventarskripten lesen.</summary>
public static class CustomValues
{
    public const int MaxLength = 1000;

    /// <summary>Der gespeicherte Wert: Zahl invariant, Datum yyyy-MM-dd, Ja/Nein als true/false, Text gekürzt.</summary>
    /// <exception cref="FormatException">Der Wert passt nicht zum Typ.</exception>
    public static string Normalize(CustomFieldKind kind, string value)
    {
        var text = value.Trim();
        switch (kind)
        {
            case CustomFieldKind.Number:
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return number.ToString(CultureInfo.InvariantCulture);
                throw new FormatException($"'{text}' is not a number.");
            case CustomFieldKind.Date:
                // Windows PowerShell 5.1 gibt Datumswerte in ConvertTo-Json als \/Date(Millisekunden)\/ aus.
                if (text.StartsWith("/Date(", StringComparison.Ordinal) && long.TryParse(text[6..].TrimEnd(')', '/').Split('+', '-')[0], out var ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
                    return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                throw new FormatException($"'{text}' is not a date.");
            case CustomFieldKind.Bool:
                return text.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "ja" => "true",
                    "false" or "0" or "no" or "nein" => "false",
                    _ => throw new FormatException($"'{text}' is not true or false."),
                };
            default:
                return text.Length <= MaxLength ? text : text[..MaxLength];
        }
    }

    /// <summary>
    /// Liest das JSON-Objekt aus der Ausgabe eines Skripts: das letzte, das am Anfang einer Zeile beginnt und sich lesen lässt
    /// (davor darf anderes stehen, auch geschweifte Klammern in Meldungen). Werte: Text, Zahl, true/false; null entfernt den
    /// Wert. Liefert Schlüssel → Wert.
    /// </summary>
    /// <exception cref="FormatException">Kein lesbares JSON-Objekt in der Ausgabe.</exception>
    public static Dictionary<string, string?> ParseOutput(string? output)
    {
        var end = output?.LastIndexOf('}') ?? -1;
        if (output is null || end < 0)
            throw new FormatException("The script printed no JSON object.");
        // Anfänge von Objekten am Zeilenanfang, das letzte zuerst; eingerückte innere Objekte zählen nicht.
        var starts = new List<int>();
        var lineStart = 0;
        for (var i = 0; i < end; i++)
        {
            if (output[i] == '\n')
                lineStart = i + 1;
            else if (output[i] == '{' && output.AsSpan(lineStart, i - lineStart).IsWhiteSpace())
                starts.Add(i);
        }
        JsonException? last = null;
        foreach (var start in Enumerable.Reverse(starts))
        {
            try
            {
                return Read(output[start..(end + 1)]);
            }
            catch (JsonException ex)
            {
                last = ex;
            }
        }
        throw new FormatException(last is null ? "The script printed no JSON object." : $"The script output is not valid JSON: {last.Message}", last);
    }

    private static Dictionary<string, string?> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("not an object");
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                // Zahlen wie geschrieben; Listen und Objekte bleiben als JSON-Text lesbar.
                _ => property.Value.GetRawText(),
            };
        }
        return result;
    }
}
