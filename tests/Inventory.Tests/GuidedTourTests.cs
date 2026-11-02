using System.Text.RegularExpressions;
using Inventory.Core;
using Inventory.Server.Services;

namespace Inventory.Tests;

/// <summary>Die Tour hält bei jedem Menüeintrag; ein neuer Eintrag ohne Halt fällt hier auf.</summary>
public class GuidedTourTests
{
    [Fact]
    public void Every_menu_entry_has_a_tour_stop()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inventory.sln")))
            dir = dir.Parent;
        var menu = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Inventory.Server", "Components", "Layout", "NavMenu.razor"));
        var steps = new GuidedTour(null!, PassthroughLocalizer.Instance).Steps().Select(s => s.Selector).ToHashSet();

        var links = Regex.Matches(menu, @"<MudNavLink\b[^>]*>").Select(m => m.Value).ToList();
        Assert.True(links.Count > 20, "Menüeinträge nicht gefunden – stimmt das Muster noch?");
        // Die Übersicht hat ihren Halt auf der Seite selbst ("Needs attention"); Plugin-Einträge kommen von außen.
        var missing = links.Where(l => !l.Contains("Href=\"\""))
            .Select(l => (Link: l, Tour: Regex.Match(l, @"data-tour=""([^""]+)""").Groups[1].Value))
            .Where(l => l.Tour.Length == 0 || !steps.Contains($"[data-tour=\"{l.Tour}\"]"))
            .Select(l => l.Link).ToList();
        Assert.True(missing.Count == 0, "Ohne Halt in der Tour:\n" + string.Join("\n", missing));
    }
}
