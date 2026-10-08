using System.Text;
using Inventory.Core;

namespace Inventory.Tests;

public sealed class SilentOptionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inventory-silent-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, params byte[][] parts)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, parts.SelectMany(p => p).ToArray());
        return path;
    }

    private static byte[] Filler(int size) => Enumerable.Repeat((byte)0x90, size).ToArray();

    [Theory]
    [InlineData("Inno Setup", false, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")]
    [InlineData("Nullsoft.NSIS.exehead", false, "/S")]
    [InlineData("InstallShield", true, "/s /v\"/qn /norestart\"")]
    [InlineData(".wixburn", false, "/quiet /norestart")]
    public void Exe_installers_are_recognised_by_ansi_or_utf16_signature(string signature, bool utf16, string expected)
    {
        var bytes = utf16 ? Encoding.Unicode.GetBytes(signature) : Encoding.ASCII.GetBytes(signature);
        var path = Write("setup.exe", "MZ"u8.ToArray(), Filler(50_000), bytes, Filler(1000));

        Assert.Equal(expected, SilentOptions.Detect(path)!.Arguments);
    }

    [Fact]
    public void Msi_needs_no_parameters_and_unknown_exe_gets_a_hint()
    {
        var msi = SilentOptions.Detect(Write("paket.MSI", Filler(10)))!;
        Assert.Equal("", msi.Arguments);
        Assert.Contains("/qn", msi.Note);

        var unknown = SilentOptions.Detect(Write("tool.exe", Filler(2000)))!;
        Assert.Equal("", unknown.Arguments);
        Assert.Contains("nicht erkannt", unknown.Note);

        Assert.Null(SilentOptions.Detect(Write("readme.txt", Filler(10))));
    }
}
