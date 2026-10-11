using Inventory.Contracts;

namespace Inventory.Tests;

public class SoftwareMatcherTests
{
    [Theory]
    [InlineData("7-Zip", null, "7-Zip 24.08 (x64)", "24.08", true)]
    [InlineData("7-zip", "24.08", "7-Zip 24.08 (x64)", "24.08.00.0", true)]
    [InlineData("7-Zip", "25.0", "7-Zip 24.08 (x64)", "24.08", false)]
    [InlineData("Mozilla Firefox", "131", "Mozilla Firefox (x64 de)", "132.0.1", true)]
    [InlineData("Firefox", null, "Mozilla Firefox (x64 de)", "132.0.1", true)]
    [InlineData("Git", null, "Git", "2.45.1", true)]
    [InlineData("Git", null, "GitHub Desktop", "3.4.2", false)]
    [InlineData("Office", null, "LibreOffice 24.2", "24.2", false)]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "14.38", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "14.40.33810.0", true)]
    [InlineData("7-Zip*", null, "7-Zip 24.08 (x64)", "24.08", true)]
    [InlineData("*Zip", null, "7-Zip 24.08 (x64)", "24.08", false)]
    [InlineData("Git*", null, "GitHub Desktop", "3.4.2", true)]
    [InlineData("", null, "7-Zip", "1", false)]
    public void A_name_matches_as_whole_words_or_as_a_pattern_and_respects_the_minimum_version(string pattern, string? minVersion, string name, string version, bool expected) =>
        Assert.Equal(expected, SoftwareMatcher.Matches(pattern, minVersion, name, version));

    [Theory]
    [InlineData("7-Zip 26.04 (x64 edition)", "7-Zip")]
    [InlineData("Notepad++ (x64)", "Notepad++")]
    [InlineData("Google Chrome", "Google Chrome")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Visual C++ 2015-2022 Redistributable (x64)")]
    public void The_base_name_drops_version_and_trailing_brackets(string name, string expected) =>
        Assert.Equal(expected, SoftwareMatcher.BaseName(name));

    [Theory]
    [InlineData("23170f69-40c1-2702-2408-000001000000")]
    [InlineData(" {23170F69-40C1-2702-2408-000001000000} ")]
    public void Product_codes_are_normalised_to_the_registry_spelling(string code) =>
        Assert.Equal("{23170F69-40C1-2702-2408-000001000000}", SoftwareMatcher.NormalizeProductCode(code));
}
