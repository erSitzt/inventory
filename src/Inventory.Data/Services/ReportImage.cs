using System.Buffers.Binary;
using System.IO.Compression;

namespace Inventory.Data.Services;

/// <summary>Ein Bild in der Form, in der es ins PDF kommt: Bilddaten mit ihrem Filter, bei Transparenz eine eigene Maske.</summary>
/// <param name="ColorSpace">DeviceRGB oder DeviceGray.</param>
/// <param name="Filter">DCTDecode (JPEG unverändert) oder FlateDecode (entpacktes PNG, neu komprimiert).</param>
/// <param name="Alpha">Deckkraft je Pixel (8 bit, FlateDecode); null = undurchsichtig.</param>
public sealed record ReportLogo(int Width, int Height, string ColorSpace, string Filter, byte[] Data, byte[]? Alpha);

/// <summary>
/// Liest das Logo für den Kopf der PDF-Berichte. Ohne Bildbibliothek: JPEG geht unverändert ins PDF, PNG wird entpackt,
/// von seinen Zeilenfiltern befreit und als Farbe plus Deckkraftmaske neu verpackt.
/// </summary>
public static class ReportImage
{
    /// <summary>Größere Dateien nimmt die Einstellung nicht an; jedes PDF trägt das Logo einmal.</summary>
    public const int MaxBytes = 512 * 1024;
    /// <summary>
    /// Gedruckt wird das Logo höchstens 140 × 30 pt groß; mehr Pixel bringen nichts. Die Grenze hält auch ein stark
    /// komprimierbares PNG unter 512 KB davon ab, beim Entpacken hunderte Megabyte zu belegen.
    /// </summary>
    public const int MaxPixels = 1024 * 1024;

    /// <exception cref="InvalidDataException">Kein unterstütztes Bild (PNG mit 8 bit je Kanal oder JPEG).</exception>
    public static ReportLogo Parse(byte[] data)
    {
        if (data.Length > MaxBytes)
            throw new InvalidDataException($"The image is larger than {MaxBytes / 1024} KB.");
        if (data.Length > 8 && data.AsSpan(0, 8).SequenceEqual(PngSignature))
            return Png(data);
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8)
            return Jpeg(data);
        throw new InvalidDataException("Neither PNG nor JPEG.");
    }

    /// <summary>Der Inhaltstyp für eine Vorschau im Browser.</summary>
    public static string ContentType(byte[] data) => data.Length > 8 && data.AsSpan(0, 8).SequenceEqual(PngSignature) ? "image/png" : "image/jpeg";

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReportLogo Jpeg(byte[] data)
    {
        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF)
                throw new InvalidDataException("Damaged JPEG.");
            if (data[i + 1] is >= 0xD0 and <= 0xD9)
                throw new InvalidDataException("JPEG without image size.");
            var marker = data[i + 1];
            // Füllbytes zwischen den Segmenten.
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 2));
            // Start of Frame (baseline, erweitert, progressiv); C4, C8 und CC sind andere Segmente.
            if (marker is 0xC0 or 0xC1 or 0xC2 && i + 10 <= data.Length)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 5));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 7));
                var components = data[i + 9];
                if (width == 0 || height == 0)
                    throw new InvalidDataException("JPEG without size.");
                if (width * height > MaxPixels)
                    throw new InvalidDataException($"The image has more than {MaxPixels:N0} pixels.");
                // CMYK-JPEGs speichern ihre Farben je nach Programm verschieden invertiert; lieber ablehnen als falsch drucken.
                var space = components switch
                {
                    1 => "DeviceGray",
                    3 => "DeviceRGB",
                    _ => throw new InvalidDataException("Only grayscale or RGB JPEGs are supported."),
                };
                return new ReportLogo(width, height, space, "DCTDecode", data, null);
            }
            i += 2 + length;
        }
        throw new InvalidDataException("JPEG without image size.");
    }

    private static ReportLogo Png(byte[] data)
    {
        int width = 0, height = 0, depth = 0, type = -1;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        var i = 8;
        while (i + 12 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
            var chunk = System.Text.Encoding.ASCII.GetString(data, i + 4, 4);
            if (length < 0 || i + 12 + length > data.Length)
                throw new InvalidDataException("Damaged PNG.");
            var body = data.AsSpan(i + 8, length);
            switch (chunk)
            {
                case "IHDR":
                    if (length < 13)
                        throw new InvalidDataException("Damaged PNG header.");
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    depth = body[8];
                    type = body[9];
                    if (body[12] != 0)
                        throw new InvalidDataException("Interlaced PNGs are not supported.");
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
            }
            if (chunk == "IEND")
                break;
            i += 12 + length;
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("PNG without a usable size.");
        if ((long)width * height > MaxPixels)
            throw new InvalidDataException($"The image has more than {MaxPixels:N0} pixels.");
        var channels = type switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("Unknown PNG color type."),
        };
        if (type == 3 ? depth is not (1 or 2 or 4 or 8) || palette is null : depth != 8)
            throw new InvalidDataException("Only PNGs with 8 bit per channel (or a palette) are supported.");

        var bitsPerPixel = channels * depth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var raw = new byte[height * (stride + 1)];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            try
            {
                zlib.ReadExactly(raw);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
            {
                throw new InvalidDataException("Damaged PNG image data.", ex);
            }
        }
        var pixels = Unfilter(raw, height, stride, bytesPerPixel);

        var gray = type is 0 or 4;
        var color = new byte[width * height * (gray ? 1 : 3)];
        var alpha = new byte[width * height];
        var opaque = true;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                byte a = 255;
                switch (type)
                {
                    case 0:
                        color[p] = pixels[row + x];
                        if (transparency is { Length: >= 2 } && transparency[1] == color[p])
                            a = 0;
                        break;
                    case 4:
                        color[p] = pixels[row + 2 * x];
                        a = pixels[row + 2 * x + 1];
                        break;
                    case 2:
                    case 6:
                        var s = row + x * channels;
                        color[3 * p] = pixels[s];
                        color[3 * p + 1] = pixels[s + 1];
                        color[3 * p + 2] = pixels[s + 2];
                        if (type == 6)
                            a = pixels[s + 3];
                        else if (transparency is { Length: >= 6 } && transparency[1] == pixels[s] && transparency[3] == pixels[s + 1] && transparency[5] == pixels[s + 2])
                            a = 0;
                        break;
                    default:
                        // Palette: Index aus 1, 2, 4 oder 8 Bit, höchstwertige Bits zuerst.
                        var bit = x * depth;
                        var index = (pixels[row + bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                        if (3 * index + 2 >= palette!.Length)
                            throw new InvalidDataException("PNG palette index out of range.");
                        color[3 * p] = palette[3 * index];
                        color[3 * p + 1] = palette[3 * index + 1];
                        color[3 * p + 2] = palette[3 * index + 2];
                        if (transparency is not null && index < transparency.Length)
                            a = transparency[index];
                        break;
                }
                alpha[p] = a;
                opaque &= a == 255;
            }
        }
        return new ReportLogo(width, height, gray ? "DeviceGray" : "DeviceRGB", "FlateDecode", Deflate(color), opaque ? null : Deflate(alpha));
    }

    /// <summary>Macht die Zeilenfilter von PNG rückgängig (None, Sub, Up, Average, Paeth).</summary>
    private static byte[] Unfilter(byte[] raw, int height, int stride, int bpp)
    {
        var result = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = y * (stride + 1) + 1;
            var target = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int left = x >= bpp ? result[target + x - bpp] : 0;
                int up = y > 0 ? result[target - stride + x] : 0;
                int upLeft = x >= bpp && y > 0 ? result[target - stride + x - bpp] : 0;
                var value = raw[source + x];
                result[target + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + (left + up) / 2),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException("Unknown PNG filter."),
                };
            }
        }
        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }
}
