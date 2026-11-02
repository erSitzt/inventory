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
        var steps = new GuidedTour(null!, PassthroughLocalizer.Instance).Steps().Select(s => s.Selector).OfType<string>().ToList();

        var links = Regex.Matches(menu, @"<MudNavLink\b[^>]*>").Select(m => m.Value).ToList();
        Assert.True(links.Count > 20, "Menüeinträge nicht gefunden – stimmt das Muster noch?");
        // Die Übersicht hat ihren Halt auf der Seite selbst ("Needs attention"); Plugin-Einträge kommen von außen.
        var entries = links.Where(l => !l.Contains("Href=\"\""))
            .Select(l => (Link: l, Tour: Regex.Match(l, @"data-tour=""([^""]+)""").Groups[1].Value)).ToList();
        var unmarked = entries.Where(e => e.Tour.Length == 0).Select(e => e.Link).ToList();
        Assert.True(unmarked.Count == 0, "Ohne data-tour:\n" + string.Join("\n", unmarked));

        // Je Eintrag ein eigener Halt, in der Reihenfolge des Menüs.
        var menuSelectors = entries.Select(e => $"[data-tour=\"{e.Tour}\"]").ToList();
        Assert.Equal(menuSelectors.Count, menuSelectors.Distinct().Count());
        Assert.Equal(menuSelectors, steps.Where(menuSelectors.Contains).ToList());
    }
}
