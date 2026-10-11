using Inventory.Core.Model;

namespace Inventory.Tests;

public class MaintenanceWindowTests
{
    private const int Weekdays = 0b0111110;
    // 2026-10-12 ist ein Montag.
    private static DateTime Monday(int hour, int minute = 0) => new(2026, 10, 12, hour, minute, 0);

    [Theory]
    [InlineData(0, 19, 0, true)]   // Montag 19:00
    [InlineData(0, 18, 59, false)]
    [InlineData(1, 5, 59, true)]   // Dienstag früh: noch das Fenster von Montag
    [InlineData(1, 6, 0, false)]
    [InlineData(5, 3, 0, true)]    // Samstag früh: Rest des Freitagsfensters
    [InlineData(5, 20, 0, false)]  // Samstagabend beginnt keines
    [InlineData(6, 23, 0, false)]  // Sonntag
    [InlineData(7, 3, 0, false)]   // Montag früh: Sonntag hat kein Fenster
    public void A_window_past_midnight_belongs_to_the_day_it_starts(int dayOffset, int hour, int minute, bool open) =>
        Assert.Equal(open, new MaintenanceWindow { DaysOfWeek = Weekdays, From = TimeSpan.FromHours(19), To = TimeSpan.FromHours(6) }.Contains(Monday(hour, minute).AddDays(dayOffset)));

    [Fact]
    public void Same_start_and_end_means_the_whole_day_and_a_daytime_window_ends_before_its_end_time()
    {
        var sunday = new MaintenanceWindow { DaysOfWeek = 1 };
        Assert.True(sunday.Contains(Monday(12).AddDays(-1)));
        Assert.False(sunday.Contains(Monday(0)));

        var lunch = new MaintenanceWindow { DaysOfWeek = Weekdays, From = TimeSpan.FromHours(12), To = TimeSpan.FromHours(13) };
        Assert.True(lunch.Contains(Monday(12, 59)));
        Assert.False(lunch.Contains(Monday(13)));
    }

    [Fact]
    public void The_next_start_skips_days_without_a_window()
    {
        var window = new MaintenanceWindow { DaysOfWeek = Weekdays, From = TimeSpan.FromHours(19), To = TimeSpan.FromHours(6) };
        Assert.Equal(Monday(19), window.NextStart(Monday(8)));
        Assert.Equal(Monday(19).AddDays(1), window.NextStart(Monday(19)));
        Assert.Equal(Monday(19).AddDays(7), window.NextStart(Monday(20).AddDays(4)));
        Assert.Null(new MaintenanceWindow().NextStart(Monday(8)));
    }
}
