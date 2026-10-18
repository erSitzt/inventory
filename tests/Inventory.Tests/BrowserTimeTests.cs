using Inventory.Server.Services;

namespace Inventory.Tests;

public class BrowserTimeTests
{
    [Fact]
    public void Times_are_shown_in_the_zone_of_the_browser()
    {
        var time = new BrowserTime();
        Assert.Equal(TimeZoneInfo.Local, time.Zone);

        time.Set("Europe/Berlin");
        var utc = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0), time.ToLocal(utc));
        Assert.Equal(new DateTime(2026, 1, 15, 11, 0, 0), time.ToLocal(new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)));
        // Ein Kalendertag des Browsers beginnt in Berlin im Sommer um 22:00 UTC des Vortags.
        Assert.Equal(new DateTime(2026, 6, 30, 22, 0, 0), time.StartOfDayUtc(new DateTime(2026, 7, 1)));

        // Unbekannte oder fehlende Namen lassen die Zone unverändert.
        time.Set("Not/AZone");
        time.Set(null);
        Assert.Equal(TimeSpan.FromHours(2), time.Zone.GetUtcOffset(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)));
        Assert.Null(BrowserTime.Resolve(new string('x', 200)));
    }
}
