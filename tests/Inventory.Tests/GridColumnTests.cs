using System.Text.RegularExpressions;

namespace Inventory.Tests;

/// <summary>
/// In MudBlazor ist eine TemplateColumn von sich aus nicht sortierbar, auch mit SortBy; ohne Sortable="true" bleibt der
/// Spaltenkopf stumm.
/// </summary>
public class GridColumnTests
{
    [Fact]
    public void Template_columns_with_a_sort_key_are_sortable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inventory.sln")))
            dir = dir.Parent;
        var components = Path.Combine(dir!.FullName, "src", "Inventory.Server", "Components");
        var tag = new Regex(@"<TemplateColumn\b[^>]*>", RegexOptions.Compiled);
        var missing = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => tag.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .Where(t => t.Tag.Contains("SortBy=") && !t.Tag.Contains("Sortable="))
            .Select(t => $"{t.File}: {t.Tag}").ToList();
        Assert.True(missing.Count == 0, "SortBy ohne Sortable=\"true\":\n" + string.Join("\n", missing));
    }
}
