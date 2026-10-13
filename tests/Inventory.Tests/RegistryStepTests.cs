using Inventory.Contracts;

namespace Inventory.Tests;

public class RegistryStepTests
{
    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Firma\Programm", RegistryData.Hive.LocalMachine, @"SOFTWARE\Firma\Programm")]
    [InlineData(@"hkey_local_machine\SOFTWARE\Firma\", RegistryData.Hive.LocalMachine, @"SOFTWARE\Firma")]
    [InlineData(@"HKCU:\Software\Firma", RegistryData.Hive.CurrentUser, @"Software\Firma")]
    [InlineData("HKU/.DEFAULT/Software", RegistryData.Hive.Users, @".DEFAULT\Software")]
    [InlineData(@" HKCR\.txt ", RegistryData.Hive.ClassesRoot, ".txt")]
    [InlineData("HKLM", RegistryData.Hive.LocalMachine, "")]
    public void The_key_is_split_into_hive_and_path(string key, RegistryData.Hive hive, string path)
    {
        Assert.Equal(hive, RegistryData.Split(key, out var actual));
        Assert.Equal(path, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(@"SOFTWARE\Firma")]
    [InlineData(@"HKCC\System")]
    public void A_key_without_a_known_hive_is_refused(string? key) =>
        Assert.Throws<InvalidDataException>(() => RegistryData.Split(key, out _));

    [Fact]
    public void Data_is_converted_to_what_the_registry_expects()
    {
        Assert.Equal("C:\\Programme", RegistryData.Convert(RegistryDataType.String, "C:\\Programme"));
        Assert.Equal("", RegistryData.Convert(RegistryDataType.ExpandString, null));
        Assert.Equal(1, RegistryData.Convert(RegistryDataType.DWord, " 1 "));
        Assert.Equal(255, RegistryData.Convert(RegistryDataType.DWord, "0xFF"));
        // Alle Bits gesetzt, wie es Vorlagen als 4294967295, 0xFFFFFFFF oder -1 schreiben.
        Assert.Equal(-1, RegistryData.Convert(RegistryDataType.DWord, "4294967295"));
        Assert.Equal(-1, RegistryData.Convert(RegistryDataType.DWord, "0xffffffff"));
        Assert.Equal(-1, RegistryData.Convert(RegistryDataType.DWord, "-1"));
        Assert.Equal(5_000_000_000L, RegistryData.Convert(RegistryDataType.QWord, "5000000000"));
        Assert.Equal(-1L, RegistryData.Convert(RegistryDataType.QWord, "-1"));
        Assert.Equal(new[] { "eins", "zwei" }, RegistryData.Convert(RegistryDataType.MultiString, "eins\r\nzwei\n"));
        Assert.Equal(new byte[] { 1, 10, 255 }, RegistryData.Convert(RegistryDataType.Binary, "01 0A,ff"));
        Assert.Equal(Array.Empty<byte>(), RegistryData.Convert(RegistryDataType.Binary, ""));
    }

    [Theory]
    [InlineData(RegistryDataType.DWord, "")]
    [InlineData(RegistryDataType.DWord, "eins")]
    [InlineData(RegistryDataType.DWord, "4294967296")]
    [InlineData(RegistryDataType.DWord, "-2147483649")]
    [InlineData(RegistryDataType.DWord, "0x1FFFFFFFF")]
    [InlineData(RegistryDataType.QWord, "1.5")]
    [InlineData(RegistryDataType.Binary, "ABC")]
    [InlineData(RegistryDataType.Binary, "ZZ")]
    public void Data_that_does_not_fit_the_type_is_refused(RegistryDataType type, string data) =>
        Assert.Throws<InvalidDataException>(() => RegistryData.Convert(type, data));

    [Theory]
    [InlineData("NetFx3", true)]
    [InlineData("Microsoft-Windows-Subsystem-Linux", true)]
    [InlineData("IIS-WebServerRole_1.0", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("NetFx3 /all", false)]
    [InlineData("a&b", false)]
    [InlineData("\"NetFx3\"", false)]
    public void Only_plain_names_pass_as_feature_names(string? name, bool valid) =>
        Assert.Equal(valid, RegistryData.IsFeatureName(name));

    [Fact]
    public void The_new_step_fields_survive_the_way_to_the_runner()
    {
        var step = new JobStep
        {
            Name = "Wert", Type = StepType.Registry, RegistryKey = @"HKLM\SOFTWARE\Firma", RegistryValueName = "Lizenz", RegistryAction = RegistryAction.SetValue,
            RegistryDataType = RegistryDataType.DWord, RegistryData = "1", Registry32Bit = true, SourcePath = @"\\server\share\setup", FeatureNames = ["NetFx3"], FeatureDisable = true,
        };

        var read = JobJson.Deserialize<JobStep>(JobJson.Serialize(step))!;

        Assert.Equal((StepType.Registry, @"HKLM\SOFTWARE\Firma", "Lizenz", RegistryDataType.DWord, "1", true),
            (read.Type, read.RegistryKey, read.RegistryValueName, read.RegistryDataType, read.RegistryData, read.Registry32Bit));
        Assert.Equal((@"\\server\share\setup", true), (read.SourcePath, read.FeatureDisable));
        Assert.Equal(["NetFx3"], read.FeatureNames);
        // Ein Schritt aus der Zeit vor diesen Feldern liest sich mit den Vorgaben.
        var old = JobJson.Deserialize<JobStep>("{\"Name\":\"alt\",\"Type\":4,\"File\":\"a\",\"Destination\":\"b\"}")!;
        Assert.Null(old.SourcePath);
        Assert.Empty(old.FeatureNames);
    }
}
