using System.Globalization;
using Inventory.Core;

namespace Inventory.Tests;

/// <summary>Datumsangaben aus WMI und Registry werden erkannt und im Format der Sprache angezeigt.</summary>
public class InventoryDatesTests
{
    [Theory]
    [InlineData("10/26/2023", 2023, 10, 26)]
    [InlineData("5/19/2026", 2026, 5, 19)]
    [InlineData("20231026", 2023, 10, 26)]
    [InlineData("2023-10-26", 2023, 10, 26)]
    [InlineData("01d9083c3e9a8000", 2022, 12, 4)]
    public void Windows_dates_are_recognised(string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), InventoryDates.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("13/45/2023")]
    public void Anything_else_is_no_date(string? text) => Assert.Null(InventoryDates.Parse(text));

    [Fact]
    public void Dates_are_shown_in_the_format_of_the_language()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("26.10.2023", InventoryDates.Format("10/26/2023"));
            Assert.Equal("2023-10-26", InventoryDates.Format("20231026", "yyyy-MM-dd"));
            Assert.Equal("unknown", InventoryDates.Format("unknown"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
