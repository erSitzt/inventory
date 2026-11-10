using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Inventory.Data.Services;

/// <summary>Eine Datei, die das Paket bei seinen Skripten mitbringt; der Pfad ist relativ, z. B. "osx-arm64/inventory-agent".</summary>
/// <param name="Source">Die Datei auf dem Server; null, wenn <paramref name="Content"/> den Inhalt liefert.</param>
public sealed record MacPackageFile(string Path, bool Executable, string? Source = null, byte[]? Content = null);

/// <summary>
/// Schreibt ein macOS-Installationspaket ohne Werkzeuge von Apple: ein „flaches“ Komponentenpaket, also ein xar-Archiv mit
/// PackageInfo und Scripts, ohne Payload. Was zu installieren ist, liegt bei den Skripten (ein mit gzip gepacktes cpio-Archiv im
/// odc-Format); macOS packt sie in einen temporären Ordner aus und startet dort postinstall als root. So braucht es keine
/// Bom-Datei, und Intune nimmt das Paket als „macOS app (PKG)“ auch unsigniert an.
/// </summary>
public static class MacPackage
{
    public const string PostinstallScript = "postinstall";

    /// <param name="identifier">Paketkennung, unter der macOS die Installation verbucht (pkgutil --pkgs).</param>
    /// <param name="files">Die Dateien neben postinstall; postinstall selbst gehört dazu.</param>
    public static void Write(Stream output, string identifier, string version, IReadOnlyList<MacPackageFile> files)
    {
        if (!files.Any(f => f.Path == PostinstallScript && f.Executable))
            throw new ArgumentException("The package needs an executable postinstall script.", nameof(files));

        using var scripts = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        using (var gzip = new GZipStream(scripts, CompressionLevel.Fastest, leaveOpen: true))
            WriteCpio(gzip, files);

        var info = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("pkg-info",
            new XAttribute("format-version", 2), new XAttribute("identifier", identifier), new XAttribute("version", version),
            new XAttribute("install-location", "/"), new XAttribute("auth", "root"), new XAttribute("postinstall-action", "none"),
            new XElement("scripts", new XElement(PostinstallScript, new XAttribute("file", "./" + PostinstallScript)))));
        var packageInfo = Encoding.UTF8.GetBytes(info.Declaration + "\n" + info.Root + "\n");

        scripts.Position = 0;
        var scriptsSha1 = SHA1.HashData(scripts);

        // Der Heap beginnt mit der Prüfsumme des gepackten Inhaltsverzeichnisses, danach folgen die Dateien. SHA-1 schreibt das
        // Format vor (Prüfsumme gegen beschädigte Archive, kein Schutz vor Manipulation; das Paket ist ohnehin unsigniert).
        const int checksumSize = 20;
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        XElement Entry(int id, string name, long offset, long length, byte[] sha1) => new("file", new XAttribute("id", id),
            new XElement("name", name), new XElement("type", "file"), new XElement("mode", "0644"),
            new XElement("uid", 0), new XElement("user", "root"), new XElement("gid", 0), new XElement("group", "wheel"),
            new XElement("data",
                new XElement("offset", offset), new XElement("length", length), new XElement("size", length),
                new XElement("encoding", new XAttribute("style", "application/octet-stream")),
                new XElement("archived-checksum", new XAttribute("style", "sha1"), Hex(sha1)),
                new XElement("extracted-checksum", new XAttribute("style", "sha1"), Hex(sha1))));
        var toc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("xar", new XElement("toc",
            new XElement("checksum", new XAttribute("style", "sha1"), new XElement("offset", 0), new XElement("size", checksumSize)),
            new XElement("creation-time", created),
            Entry(1, "PackageInfo", checksumSize, packageInfo.Length, SHA1.HashData(packageInfo)),
            Entry(2, "Scripts", checksumSize + packageInfo.Length, scripts.Length, scriptsSha1))));
        var tocBytes = Encoding.UTF8.GetBytes(toc.Declaration + "\n" + toc.ToString(SaveOptions.DisableFormatting));
        using var packedToc = new MemoryStream();
        using (var zlib = new ZLibStream(packedToc, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(tocBytes);
        var packed = packedToc.ToArray();

        // Kopf: Kennung "xar!", Kopfgröße 28, Version 1, Länge des Inhaltsverzeichnisses gepackt und ungepackt, Prüfsumme SHA-1.
        Span<byte> header = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x78617221);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 28);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 1);
        BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)packed.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header[16..], (ulong)tocBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[24..], 1);
        output.Write(header);
        output.Write(packed);
        output.Write(SHA1.HashData(packed));
        output.Write(packageInfo);
        scripts.Position = 0;
        scripts.CopyTo(output);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>cpio im odc-Format („portable ASCII“): erst der Ordner ".", dann je Unterordner einmal, jeweils vor seinen Dateien.</summary>
    private static void WriteCpio(Stream stream, IReadOnlyList<MacPackageFile> files)
    {
        var inode = 0;
        void Entry(string name, int mode, long size, Stream? content)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var header = "070707" + Octal(0, 6) + Octal(++inode, 6) + Octal(mode, 6) + Octal(0, 6) + Octal(0, 6)
                         + Octal(mode >> 12 == 4 ? 2 : 1, 6) + Octal(0, 6) + Octal(0, 11) + Octal(nameBytes.Length + 1, 6) + Octal(size, 11);
            stream.Write(Encoding.ASCII.GetBytes(header));
            stream.Write(nameBytes);
            stream.WriteByte(0);
            content?.CopyTo(stream);
        }

        var directory = Convert.ToInt32("040755", 8);
        Entry(".", directory, 0, null);
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Path.StartsWith('/') || file.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new ArgumentException($"Invalid path in the package: {file.Path}", nameof(files));
            var parts = file.Path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var folder = string.Join('/', parts[..i]);
                if (created.Add(folder))
                    Entry("./" + folder, directory, 0, null);
            }
            var mode = Convert.ToInt32(file.Executable ? "100755" : "100644", 8);
            using Stream content = file.Source is not null ? File.OpenRead(file.Source) : new MemoryStream(file.Content ?? []);
            Entry("./" + file.Path, mode, content.Length, content);
        }
        Entry("TRAILER!!!", 0, 0, null);
    }

    private static string Octal(long value, int width)
    {
        var text = Convert.ToString(value, 8);
        return text.Length <= width ? text.PadLeft(width, '0') : throw new InvalidDataException($"{value} does not fit into the cpio header.");
    }
}
