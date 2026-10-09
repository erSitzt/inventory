using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Inventory.Tests;

/// <summary>
/// Die Oberfläche ist englisch im Quelltext und wird über Resources/SharedResource.&lt;kultur&gt;.resx übersetzt.
/// Diese Tests halten die Übersetzung vollständig und den Quelltext frei von fest verdrahteten deutschen Texten.
/// </summary>
public class LocalizationTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inventory.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Inventory.sln nicht gefunden");
    }

    private static IEnumerable<string> SourceFiles() =>
        new[] { "src/Inventory.Server", "src/Inventory.Data", "src/Inventory.Core", "src/Inventory.Remoting" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".razor")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static Dictionary<string, string> Translations(string culture)
    {
        var resx = Path.Combine(Root, "src", "Inventory.Core", "Resources", $"SharedResource.{culture}.resx");
        return XDocument.Load(resx).Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
    }

    /// <summary>Jeder im Quelltext verwendete Text (L["…"]) hat eine deutsche Übersetzung.</summary>
    [Fact]
    public void Every_localized_text_has_a_german_translation()
    {
        var german = Translations("de");
        var keys = new HashSet<string>();
        // L["Text"] und L["Text {0}", args] – Schlüssel sind einfache Zeichenketten ohne Interpolation.
        var pattern = new Regex(@"\bL\[""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        foreach (var file in SourceFiles())
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                keys.Add(Regex.Unescape(match.Groups[1].Value));
        }
        Assert.True(keys.Count > 500, $"nur {keys.Count} Texte gefunden – stimmt das Muster noch?");

        var missing = keys.Where(k => !german.ContainsKey(k)).Order().ToList();
        Assert.True(missing.Count == 0, "Ohne deutsche Übersetzung:\n" + string.Join("\n", missing));
    }

    /// <summary>Platzhalter müssen in der Übersetzung erhalten bleiben, sonst wirft string.Format zur Laufzeit.</summary>
    [Fact]
    public void Translations_keep_their_placeholders()
    {
        var placeholders = new Regex(@"\{\d+\}");
        var broken = Translations("de")
            .Where(kv => !placeholders.Matches(kv.Key).Select(m => m.Value).Order().SequenceEqual(placeholders.Matches(kv.Value).Select(m => m.Value).Order()))
            .Select(kv => kv.Key).ToList();
        Assert.True(broken.Count == 0, "Platzhalter weichen ab:\n" + string.Join("\n", broken));
    }

    /// <summary>Kein deutscher Text mehr fest im Quelltext (außerhalb von Kommentaren) – alles läuft über die Übersetzung.</summary>
    [Fact]
    public void Source_texts_are_english()
    {
        var comment = new Regex(@"^\s*(//|///|\*|/\*|@\*|#)");
        var german = new Regex(@"""[^""\n]*[äöüÄÖÜß][^""\n]*""|>[^<\n@]*[äöüÄÖÜß][^<\n]*<");
        var hits = new List<string>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (comment.IsMatch(lines[i]) || lines[i].Contains("<summary>") || lines[i].Contains("<param") || lines[i].Contains("<remarks>"))
                    continue;
                if (german.IsMatch(lines[i]))
                    hits.Add($"{Path.GetRelativePath(Root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "Deutscher Text im Quelltext:\n" + string.Join("\n", hits));
    }
}
