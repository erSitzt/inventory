using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Inventory.Contracts;
using Inventory.Data.Services;

namespace Inventory.Tests;

/// <summary>Das Installationspaket für Macs (Intune, Jamf): installiert den Agenten, ohne dass der Mac den Server erreicht.</summary>
public class MacPackageTests
{
    [Fact]
    public async Task The_package_brings_both_agents_and_the_configuration_and_needs_no_server()
    {
        using var db = new TestDb();
        var agents = new AgentService(db, new SettingsService(db), TimeProvider.System);
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var arm = Path.Combine(folder.FullName, "arm");
            var intel = Path.Combine(folder.FullName, "intel");
            File.WriteAllText(arm, "agent for Apple Silicon");
            File.WriteAllText(intel, "agent for Intel");
            AgentService.MacAgentBinary[] binaries = [new(RunnerRuntimes.MacArm64, arm, "hash-arm"), new(RunnerRuntimes.MacX64, intel, "hash-intel")];
            using var output = new MemoryStream();
            await Assert.ThrowsAsync<InvalidOperationException>(() => agents.BuildMacPackageAsync(output, binaries, "1.2.3"));
            await agents.SetServerUrlAsync("https://inventory.example.local:5100");
            await Assert.ThrowsAsync<InvalidOperationException>(() => agents.BuildMacPackageAsync(output, [], "1.2.3"));
            await agents.BuildMacPackageAsync(output, binaries, "1.2.3");

            var files = ReadXar(output.ToArray());
            Assert.Equal(["PackageInfo", "Scripts"], files.Keys.Order());
            var info = XDocument.Parse(Encoding.UTF8.GetString(files["PackageInfo"])).Root!;
            Assert.Equal((AgentProtocol.MacAgentLabel, "1.2.3", "/", "root"),
                (info.Attribute("identifier")?.Value, info.Attribute("version")?.Value, info.Attribute("install-location")?.Value, info.Attribute("auth")?.Value));
            Assert.Equal("./postinstall", info.Element("scripts")?.Element("postinstall")?.Attribute("file")?.Value);

            var scripts = ReadCpio(files["Scripts"]);
            Assert.Equal([".", "./postinstall", "./osx-arm64", "./osx-arm64/inventory-agent", "./osx-x64", "./osx-x64/inventory-agent"], scripts.Select(e => e.Name));
            var byName = scripts.ToDictionary(e => e.Name);
            Assert.Equal("agent for Apple Silicon", Encoding.UTF8.GetString(byName["./osx-arm64/inventory-agent"].Content));
            Assert.Equal("agent for Intel", Encoding.UTF8.GetString(byName["./osx-x64/inventory-agent"].Content));
            Assert.Equal(Convert.ToInt32("100755", 8), byName["./osx-arm64/inventory-agent"].Mode);
            Assert.Equal(Convert.ToInt32("100755", 8), byName["./postinstall"].Mode);
            Assert.Equal(Convert.ToInt32("040755", 8), byName["./osx-x64"].Mode);

            var postinstall = Encoding.UTF8.GetString(byName["./postinstall"].Content);
            Assert.StartsWith("#!/bin/bash\n", postinstall);
            Assert.DoesNotContain("\r", postinstall);
            Assert.Contains("https://inventory.example.local:5100", postinstall);
            Assert.Contains(await agents.GetEnrollmentTokenAsync(), postinstall);
            Assert.Contains("osx-arm64) HASH='hash-arm' ;;", postinstall);
            Assert.Contains("osx-x64) HASH='hash-intel' ;;", postinstall);
            // Der Agent kommt aus dem Paket, nicht vom Server.
            Assert.DoesNotContain("curl", postinstall);
            Assert.Contains("codesign --force --sign - \"$DOWNLOAD\"", postinstall);
            Assert.Contains("launchctl bootstrap system \"$PLIST\"", postinstall);
            Bash.AssertSyntax(postinstall);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Paths_outside_the_scripts_folder_are_refused()
    {
        MacPackageFile postinstall = new(MacPackage.PostinstallScript, true, Content: "#!/bin/bash\n"u8.ToArray());
        Assert.Throws<ArgumentException>(() => MacPackage.Write(Stream.Null, "x", "1", [postinstall, new("../evil", false, Content: [])]));
        Assert.Throws<ArgumentException>(() => MacPackage.Write(Stream.Null, "x", "1", [new("other", true, Content: [])]));
    }

    /// <summary>
    /// Nur im CI-Job auf dem Mac: baut das Paket mit den eben veröffentlichten Agenten; der Job installiert es danach mit
    /// <c>installer</c>. Die Server-Adresse zeigt ins Leere – der Agent muss trotzdem starten und es weiter versuchen.
    /// </summary>
    [Fact]
    public async Task Package_with_the_published_agents_for_the_macos_job()
    {
        var agentDirectory = Environment.GetEnvironmentVariable("INVENTORY_MAC_AGENT_DIR");
        var target = Environment.GetEnvironmentVariable("INVENTORY_MAC_PKG");
        if (string.IsNullOrEmpty(agentDirectory) || string.IsNullOrEmpty(target))
            return;
        using var db = new TestDb();
        var agents = new AgentService(db, new SettingsService(db), TimeProvider.System);
        await agents.SetServerUrlAsync("http://127.0.0.1:9");
        var binaries = new[] { RunnerRuntimes.MacArm64, RunnerRuntimes.MacX64 }
            .Select(runtime => new AgentService.MacAgentBinary(runtime, Path.Combine(agentDirectory, runtime, AgentProtocol.MacAgentFile), "ci"))
            .Where(b => File.Exists(b.File)).ToList();
        Assert.NotEmpty(binaries);
        await using var output = File.Create(target);
        await agents.BuildMacPackageAsync(output, binaries, "0.0.0");
    }

    /// <summary>Liest ein xar-Archiv, wie macOS es tut: Kopf, gepacktes Inhaltsverzeichnis, Prüfsummen von Verzeichnis und Dateien.</summary>
    private static Dictionary<string, byte[]> ReadXar(byte[] archive)
    {
        var header = archive.AsSpan();
        Assert.Equal(0x78617221u, BinaryPrimitives.ReadUInt32BigEndian(header));
        Assert.Equal(28, BinaryPrimitives.ReadUInt16BigEndian(header[4..]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(header[6..]));
        var packedLength = (int)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        var plainLength = (int)BinaryPrimitives.ReadUInt64BigEndian(header[16..]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(header[24..]));

        var packed = archive[28..(28 + packedLength)];
        using var plain = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
            zlib.CopyTo(plain);
        Assert.Equal(plainLength, plain.Length);
        var toc = XDocument.Parse(Encoding.UTF8.GetString(plain.ToArray())).Root!.Element("toc")!;

        var heap = 28 + packedLength;
        byte[] Slice(XElement data, string offsetName, string lengthName)
        {
            var offset = heap + int.Parse(data.Element(offsetName)!.Value);
            return archive[offset..(offset + int.Parse(data.Element(lengthName)!.Value))];
        }
        var checksum = toc.Element("checksum")!;
        Assert.Equal("sha1", checksum.Attribute("style")?.Value);
        Assert.Equal(SHA1.HashData(packed), Slice(checksum, "offset", "size"));

        var files = new Dictionary<string, byte[]>();
        foreach (var file in toc.Elements("file"))
        {
            Assert.Equal("file", file.Element("type")?.Value);
            var data = file.Element("data")!;
            Assert.Equal("application/octet-stream", data.Element("encoding")?.Attribute("style")?.Value);
            var content = Slice(data, "offset", "length");
            Assert.Equal(content.Length, int.Parse(data.Element("size")!.Value));
            var sha1 = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
            Assert.Equal(sha1, data.Element("archived-checksum")?.Value);
            Assert.Equal(sha1, data.Element("extracted-checksum")?.Value);
            files.Add(file.Element("name")!.Value, content);
        }
        // Nach der letzten Datei endet das Archiv.
        Assert.Equal(archive.Length, heap + toc.Elements("file").Max(f => int.Parse(f.Element("data")!.Element("offset")!.Value) + int.Parse(f.Element("data")!.Element("length")!.Value)));
        return files;
    }

    /// <summary>Liest ein mit gzip gepacktes cpio-Archiv im odc-Format (76 Zeichen Kopf, Zahlen oktal).</summary>
    private static List<(string Name, int Mode, byte[] Content)> ReadCpio(byte[] packed)
    {
        using var plain = new MemoryStream();
        using (var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
            gzip.CopyTo(plain);
        var bytes = plain.ToArray();
        var entries = new List<(string, int, byte[])>();
        var position = 0;
        while (true)
        {
            var header = Encoding.ASCII.GetString(bytes, position, 76);
            Assert.Equal("070707", header[..6]);
            long Field(int start, int length) => Convert.ToInt64(header.Substring(start, length), 8);
            var (mode, nameSize, size) = ((int)Field(18, 6), (int)Field(59, 6), (int)Field(65, 11));
            var name = Encoding.UTF8.GetString(bytes, position + 76, nameSize - 1);
            Assert.Equal(0, bytes[position + 76 + nameSize - 1]);
            position += 76 + nameSize;
            if (name == "TRAILER!!!")
                break;
            entries.Add((name, mode, bytes[position..(position + size)]));
            position += size;
        }
        Assert.Equal(bytes.Length, position);
        return entries;
    }
}

/// <summary>Prüft Shell-Skripte mit bash -n, wo es bash gibt (Linux, macOS).</summary>
internal static class Bash
{
    public static void AssertSyntax(string script)
    {
        if (!File.Exists("/bin/bash"))
            return;
        var info = new ProcessStartInfo("/bin/bash", "-n") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        using var bash = Process.Start(info)!;
        bash.StandardInput.Write(script);
        bash.StandardInput.Close();
        var errors = bash.StandardError.ReadToEnd();
        Assert.True(bash.WaitForExit(10_000), "bash -n did not finish");
        Assert.True(bash.ExitCode == 0, errors);
    }
}
