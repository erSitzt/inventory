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
/// PackageInfo, Bom, Payload und Scripts. Was zu installieren ist, liegt bei den Skripten (ein mit gzip gepacktes cpio-Archiv im
/// odc-Format); macOS packt sie in einen temporären Ordner aus und startet dort postinstall als root. Die Payload ist leer (nur
/// der Ordner "/", wie bei <c>pkgbuild --root</c> mit leerem Ordner): ohne Payload verbuchte macOS das Paket nicht
/// (<c>pkgutil --pkgs</c>), und daran erkennt Intune die Installation. Intune nimmt das Paket als „macOS app (PKG)“ auch unsigniert an.
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

        // Wie pkgbuild es schreibt; die Payload enthält genau einen Eintrag, den Ordner "." (= "/").
        var info = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("pkg-info",
            new XAttribute("overwrite-permissions", "true"), new XAttribute("relocatable", "false"),
            new XAttribute("identifier", identifier), new XAttribute("postinstall-action", "none"), new XAttribute("version", version),
            new XAttribute("format-version", 2), new XAttribute("install-location", "/"), new XAttribute("auth", "root"),
            new XElement("payload", new XAttribute("numberOfFiles", 1), new XAttribute("installKBytes", 0)),
            new XElement("scripts", new XElement(PostinstallScript, new XAttribute("file", "./" + PostinstallScript), new XAttribute("timeout", 600)))));
        var packageInfo = Encoding.UTF8.GetBytes(info.Declaration + "\n" + info.Root + "\n");
        using var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionLevel.Optimal, leaveOpen: true))
            WriteCpio(gzip, []);

        // Der Heap beginnt mit der Prüfsumme des gepackten Inhaltsverzeichnisses, danach folgen die Dateien. SHA-1 schreibt das
        // Format vor (Prüfsumme gegen beschädigte Archive, kein Schutz vor Manipulation; das Paket ist ohnehin unsigniert).
        const int checksumSize = 20;
        (string Name, Stream Data)[] entries =
        [
            ("PackageInfo", new MemoryStream(packageInfo)),
            ("Bom", new MemoryStream(EmptyBom.Value)),
            ("Payload", payload),
            ("Scripts", scripts),
        ];
        long offset = checksumSize;
        var tocFiles = new List<XElement>();
        foreach (var (name, data) in entries)
        {
            data.Position = 0;
            var sha1 = Hex(SHA1.HashData(data));
            tocFiles.Add(new XElement("file", new XAttribute("id", tocFiles.Count + 1),
                new XElement("name", name), new XElement("type", "file"), new XElement("mode", "0644"),
                new XElement("uid", 0), new XElement("user", "root"), new XElement("gid", 0), new XElement("group", "wheel"),
                new XElement("data",
                    new XElement("offset", offset), new XElement("length", data.Length), new XElement("size", data.Length),
                    new XElement("encoding", new XAttribute("style", "application/octet-stream")),
                    new XElement("archived-checksum", new XAttribute("style", "sha1"), sha1),
                    new XElement("extracted-checksum", new XAttribute("style", "sha1"), sha1))));
            offset += data.Length;
        }
        var toc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("xar", new XElement("toc",
            new XElement("checksum", new XAttribute("style", "sha1"), new XElement("offset", 0), new XElement("size", checksumSize)),
            new XElement("creation-time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")),
            tocFiles)));
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
        foreach (var (_, data) in entries)
        {
            data.Position = 0;
            data.CopyTo(output);
        }
    }

    /// <summary>
    /// Die Bom (Stückliste) einer Payload, die nur den Ordner "." (drwxr-xr-x root wheel) enthält; von <c>mkbom</c> über
    /// <c>pkgbuild --root</c> mit leerem Ordner erzeugt, mit gzip gepackt (ausgepackt 57 KB, fast nur Nullen). macOS legt sie
    /// mit dem Beleg ab.
    /// </summary>
    private static readonly Lazy<byte[]> EmptyBom = new(() =>
    {
        using var packed = new GZipStream(new MemoryStream(Convert.FromBase64String(EmptyBomGzipBase64)), CompressionMode.Decompress);
        using var bom = new MemoryStream();
        packed.CopyTo(bom);
        return bom.ToArray();
    });

    private const string EmptyBomGzipBase64 =
        "H4sIAAAAAAACA+3dMWtTURQA4JNnk2jbWBtwKwjpLkg0k0PsZFFRKHUUBVOsYELbDCIIToJbwdFBcPQ/6P/wRzj5B7wvNy8NFUEQ"
        + "UcP3wc1553Lu45JkyeFxs3X3zs54dDiIiFoa6xFv9iN2jyM2Oym/Hiy68nNvTq6Ge6NpXo4YHw6q70U94kJVGytHKdbm7tBKY9Ub"
        + "CQAAAItlrjNwZtYZmJs8m8arWfXlmE6vPB48P9U6WI/pegAAAGBxzHUJlmc//ecm19LoV8Xnc8Pgb6uXe2tujZ5t5yckivq9R+Mn"
        + "R+lqqXnz9vaw3GVEo3G/ujzX2Nl/Mehd/Qf2DgAAAPxhRS3Wbnytsqff3n5OoX/SFPj99kB5h+LUnfo5LH88mep+Si8bEe1eihfT"
        + "kl7uvRQbOW8fTPOyuJXyW3m+8yEmT3J0DnK++SDXdVOMS6nuYYqNFPdi8tRH53Wu677M+yiupLiU5t/lCAAAAAAAAAAAAAAAAAAA"
        + "AAAAAAAA8FP1iOJaTM5b7byPyd/ftoc5Fq1pLA9IXf1xqfNYAQAAAAAAAAAAAAAAAAAAAAAAAACA/0rj189j7X6J2D2uFn4HwdFh"
        + "BfneAAA=";

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>cpio im odc-Format („portable ASCII“): erst der Ordner ".", dann je Unterordner einmal, jeweils vor seinen Dateien.</summary>
    private static void WriteCpio(Stream stream, IReadOnlyList<MacPackageFile> files)
    {
        var inode = 0;
        long written = 0;
        void Entry(string name, int mode, long size, Stream? content)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var header = "070707" + Octal(0, 6) + Octal(++inode, 6) + Octal(mode, 6) + Octal(0, 6) + Octal(0, 6)
                         + Octal(mode >> 12 == 4 ? 2 : 1, 6) + Octal(0, 6) + Octal(0, 11) + Octal(nameBytes.Length + 1, 6) + Octal(size, 11);
            stream.Write(Encoding.ASCII.GetBytes(header));
            stream.Write(nameBytes);
            stream.WriteByte(0);
            content?.CopyTo(stream);
            written += header.Length + nameBytes.Length + 1 + size;
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
        // Wie cpio selbst: mit Nullen auf volle 512-Byte-Blöcke auffüllen.
        stream.Write(new byte[(512 - written % 512) % 512]);
    }

    private static string Octal(long value, int width)
    {
        var text = Convert.ToString(value, 8);
        return text.Length <= width ? text.PadLeft(width, '0') : throw new InvalidDataException($"{value} does not fit into the cpio header.");
    }
}
