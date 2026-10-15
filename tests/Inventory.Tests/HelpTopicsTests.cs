using Inventory.Core;
using Inventory.Server.Services;
using Xunit;

namespace Inventory.Tests;

public class HelpTopicsTests
{
    /// <summary>Jede Seite aus dem Menü bekommt ihre eigene Hilfe, nicht die der Übersicht.</summary>
    [Theory]
    [InlineData("computers", "Computers")]
    [InlineData("computers/12", "Computer details")]
    [InlineData("computers?filter=online", "Computers")]
    [InlineData("run?ids=1,2", "Remote command")]
    [InlineData("collections", "Collections")]
    [InlineData("packages", "Packages")]
    [InlineData("packages/3", "Package")]
    [InlineData("library", "Package library")]
    [InlineData("deployments", "Deployments")]
    [InlineData("deployments/7", "Deployment")]
    [InlineData("schedules", "Schedules")]
    [InlineData("software", "Software")]
    [InlineData("licenses", "Licences")]
    [InlineData("updates", "Updates")]
    [InlineData("changes", "Changes")]
    [InlineData("reports/4", "Reports")]
    [InlineData("credentials", "Credentials")]
    [InlineData("notifications", "Notifications")]
    [InlineData("tokens", "API tokens")]
    [InlineData("audit", "Audit log")]
    [InlineData("settings#updates", "Settings")]
    [InlineData("", "Overview")]
    [InlineData("not-found", "Overview")]
    public void PicksTopicByPath(string path, string title)
    {
        var topic = HelpTopics.For(path, PassthroughLocalizer.Instance);

        Assert.Equal(title, topic.Title);
        Assert.NotEmpty(topic.Intro);
        Assert.NotEmpty(topic.Tips);
    }
}
