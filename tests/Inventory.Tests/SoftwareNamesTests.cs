using Inventory.Core;

namespace Inventory.Tests;

public class SoftwareNamesTests
{
    [Theory]
    [InlineData("7-Zip 24.08 (x64)", "24.08", "7-Zip 26.04 (x64)", "26.04", true)]
    [InlineData("Python 3.12.1 (64-bit)", "3.12.1150.0", "Python 3.11.9 (64-bit)", "3.11.9150.0", true)]
    [InlineData("Google Chrome", "139.0.7258.67", "Google Chrome", "141.0.7390.55", true)]
    [InlineData("Mozilla Firefox (x64 de)", "131.0", "Mozilla Firefox (x64 de)", "132.0.1", true)]
    [InlineData("Microsoft Office Professional Plus 2019", "16.0.10416", "Microsoft Office Professional Plus 2021", "16.0.14332", false)]
    [InlineData("Visual Studio 2019", "16.11", "Visual Studio 2022", "17.8", false)]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33130", "14.38.33130", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "14.40.33810", true)]
    [InlineData("Java 8 Update 401", "8.0.4010.10", "Java 8 Update 411", "8.0.4110.9", false)]
    public void Versions_of_the_same_program_share_a_key_but_generations_do_not(string a, string va, string b, string vb, bool same) =>
        Assert.Equal(same, SoftwareNames.Key(a, va) == SoftwareNames.Key(b, vb));

    [Theory]
    [InlineData("7-Zip 24.08 (x64)", "24.08", "7-Zip (x64)")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33130", "14.38.33130", "Microsoft Visual C++ 2015-2022 Redistributable (x64)")]
    [InlineData("Google Chrome", "141.0", "Google Chrome")]
    [InlineData("Microsoft Office Professional Plus 2019", "16.0", "Microsoft Office Professional Plus 2019")]
    public void Display_name_drops_the_version_but_keeps_the_rest(string name, string version, string expected) =>
        Assert.Equal(expected, SoftwareNames.Display(name, version));
}
