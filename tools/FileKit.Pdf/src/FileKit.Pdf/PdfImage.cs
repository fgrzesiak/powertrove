using System.Buffers.Binary;
using System.IO.Compression;

namespace FileKit.Pdf;

public enum PdfImageKind { Jpeg, Png }

/// <summary>Limits for images embedded into PDFs.</summary>
public sealed record PdfImageLimits
{
    public long MaxFileBytes { get; init; } = 200L * 1024 * 1024;
    public long MaxPixels { get; init; } = 150_000_000;

    public static PdfImageLimits Default { get; } = new();
}

/// <summary>
/// A JPEG or PNG prepared for embedding. JPEG data is embedded unchanged
/// (DCTDecode, no re-encoding, no quality loss). PNG data without alpha is embedded
/// with its original compression; PNGs with transparency are decoded into a color
/// image and a soft mask. EXIF orientation of JPEGs is honored on the page.
/// </summary>
public sealed class PdfImage
{
    internal readonly byte[] _data;
    private readonly PngInfo? _png;

    private PdfImage(PdfImageKind kind, byte[] data, int width, int height, int orientation, double dpiX, double dpiY, int components, bool adobeInverted, PngInfo? png)
    {
        Kind = kind;
        _data = data;
        Width = width;
        Height = height;
        Orientation = orientation;
        DpiX = dpiX;
        DpiY = dpiY;
        Components = components;
        AdobeInverted = adobeInverted;
        _png = png;
    }

    public PdfImageKind Kind { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>EXIF orientation 1..8 (1 = as stored).</summary>
    public int Orientation { get; }
    public double DpiX { get; }
    public double DpiY { get; }
    internal int Components { get; }
    private bool AdobeInverted { get; }

    /// <summary>Width/height after applying the orientation.</summary>
    public (int Width, int Height) DisplaySize => Orientation >= 5 ? (Height, Width) : (Width, Height);

    public static PdfImage Load(string path, PdfImageLimits? limits = null)
    {
        limits ??= PdfImageLimits.Default;
        var info = new FileInfo(path);
        if (info.Length > limits.MaxFileBytes)
            throw new PdfLimitExceededException("The image exceeds the maximum allowed size.");
        return Load(File.ReadAllBytes(path), limits);
    }

    public static PdfImage Load(byte[] data, PdfImageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        limits ??= PdfImageLimits.Default;
        if (data.LongLength > limits.MaxFileBytes)
            throw new PdfLimitExceededException("The image exceeds the maximum allowed size.");

        try
        {
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return LoadJpeg(data, limits);
            if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                return LoadPng(data, limits);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            throw new PdfFormatException("The image is damaged.", ex);
        }

        throw new PdfFormatException("Only JPEG and PNG images can be added to a PDF.");
    }

    // ---- JPEG --------------------------------------------------------------------

    private static PdfImage LoadJpeg(byte[] data, PdfImageLimits limits)
    {
        int position = 2;
        int orientation = 1;
        double dpiX = 0, dpiY = 0;
        bool adobe = false;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF) throw new PdfFormatException("The JPEG image is damaged.");
            byte marker = data[position + 1];
            position += 2;
            if (marker == 0xFF) { position--; continue; }
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (marker == 0xD9 || marker == 0xDA) break;

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position));
            if (length < 2 || position + length > data.Length) throw new PdfFormatException("The JPEG image is damaged.");
            var segment = data.AsSpan(position + 2, length - 2);

            if (marker == 0xE0 && segment.Length >= 12 && segment[..5].SequenceEqual("JFIF\0"u8))
            {
                int units = segment[7];
                int x = BinaryPrimitives.ReadUInt16BigEndian(segment[8..]);
                int y = BinaryPrimitives.ReadUInt16BigEndian(segment[10..]);
                if (units == 1 && x > 0 && y > 0) { dpiX = x; dpiY = y; }
                else if (units == 2 && x > 0 && y > 0) { dpiX = x * 2.54; dpiY = y * 2.54; }
            }
            else if (marker == 0xE1 && segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                orientation = ReadExifOrientation(segment[6..]);
            }
            else if (marker == 0xEE && segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8))
            {
                adobe = true;
            }
            else if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (marker is >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                    throw new PdfFormatException("Arithmetic-coded JPEG images are not supported.");
                if (segment.Length < 6) throw new PdfFormatException("The JPEG image is damaged.");
                int precision = segment[0];
                int height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                int components = segment[5];
                if (precision != 8) throw new PdfFormatException("Only 8-bit JPEG images are supported.");
                if (width == 0 || height == 0 || components is not (1 or 3 or 4)) throw new PdfFormatException("The JPEG image is damaged.");
                if ((long)width * height > limits.MaxPixels) throw new PdfLimitExceededException("The image has too many pixels.");
                // Remaining markers (APPn after SOF) are rare; dimensions are what matter.
                return new PdfImage(PdfImageKind.Jpeg, data, width, height, orientation, dpiX, dpiY, components, adobe && components == 4, null);
            }

            position += length;
        }

        throw new PdfFormatException("The JPEG image has no frame header.");
    }

    private static int ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) return 1;
        uint ifd = little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]);
        if (ifd + 2 > tiff.Length) return 1;
        int count = little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff[(int)ifd..]) : BinaryPrimitives.ReadUInt16BigEndian(tiff[(int)ifd..]);
        for (int i = 0; i < count && i < 512; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            int tag = little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff[entry..]) : BinaryPrimitives.ReadUInt16BigEndian(tiff[entry..]);
            if (tag == 0x0112)
            {
                int value = little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff[(entry + 8)..]) : BinaryPrimitives.ReadUInt16BigEndian(tiff[(entry + 8)..]);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }
        return 1;
    }

    // ---- PNG ---------------------------------------------------------------------

    private sealed record PngInfo(int BitDepth, int ColorType, byte[] Idat, byte[]? Palette, byte[]? Transparency);

    private static PdfImage LoadPng(byte[] data, PdfImageLimits limits)
    {
        int position = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null, transparency = null;
        double dpiX = 0, dpiY = 0;
        using var idat = new MemoryStream();

        while (position + 12 <= data.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            if (length < 0 || position + 12L + length > data.Length) throw new PdfFormatException("The PNG image is damaged.");
            var type = data.AsSpan(position + 4, 4);
            var chunk = data.AsSpan(position + 8, length);

            if (type.SequenceEqual("IHDR"u8) && length >= 13)
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
                bitDepth = chunk[8];
                colorType = chunk[9];
                interlace = chunk[12];
            }
            else if (type.SequenceEqual("PLTE"u8)) palette = chunk.ToArray();
            else if (type.SequenceEqual("tRNS"u8)) transparency = chunk.ToArray();
            else if (type.SequenceEqual("pHYs"u8) && length >= 9 && chunk[8] == 1)
            {
                dpiX = BinaryPrimitives.ReadUInt32BigEndian(chunk) * 0.0254;
                dpiY = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]) * 0.0254;
            }
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(chunk);
            else if (type.SequenceEqual("IEND"u8)) break;

            position += 12 + length;
        }

        if (width <= 0 || height <= 0 || colorType < 0 || idat.Length == 0)
            throw new PdfFormatException("The PNG image is damaged.");
        if ((long)width * height > limits.MaxPixels)
            throw new PdfLimitExceededException("The image has too many pixels.");
        if (interlace != 0)
            throw new PdfFormatException("Interlaced PNG images are not supported.");
        bool validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => bitDepth is 8 or 16,
            _ => false
        };
        if (!validDepth) throw new PdfFormatException("The PNG image uses an unsupported color format.");
        if (colorType == 3 && palette is null) throw new PdfFormatException("The PNG image is damaged.");

        return new PdfImage(PdfImageKind.Png, data, width, height, 1, dpiX, dpiY, 0, false,
            new PngInfo(bitDepth, colorType, idat.ToArray(), palette, transparency));
    }

    // ---- XObject creation --------------------------------------------------------------

    internal PdfStream CreateXObject(PdfDocumentBuilder.ObjectTable table)
    {
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = new PdfInteger(Width),
            ["Height"] = new PdfInteger(Height)
        };

        if (Kind == PdfImageKind.Jpeg)
        {
            dict["ColorSpace"] = new PdfName(Components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" });
            dict["BitsPerComponent"] = new PdfInteger(8);
            dict["Filter"] = new PdfName("DCTDecode");
            if (AdobeInverted)
                dict["Decode"] = new PdfArray(Enumerable.Range(0, 8).Select(i => (PdfObject)new PdfInteger(i % 2 == 0 ? 1 : 0)));
            return new PdfStream(dict, _data);
        }

        var png = _png!;
        bool hasAlphaChannel = png.ColorType is 4 or 6;
        bool paletteAlpha = png.ColorType == 3 && png.Transparency is { } t && t.Any(a => a < 255);

        if (!hasAlphaChannel && !paletteAlpha)
        {
            // Embed the original compressed data with PNG predictors: no decoding at all.
            int colors = png.ColorType == 2 ? 3 : 1;
            dict["ColorSpace"] = ColorSpaceFor(png);
            dict["BitsPerComponent"] = new PdfInteger(png.BitDepth);
            dict["Filter"] = new PdfName("FlateDecode");
            dict["DecodeParms"] = new PdfDictionary
            {
                ["Predictor"] = new PdfInteger(15),
                ["Colors"] = new PdfInteger(colors),
                ["BitsPerComponent"] = new PdfInteger(png.BitDepth),
                ["Columns"] = new PdfInteger(Width)
            };
            if (png.Transparency is { } key && png.ColorType is 0 or 2)
                dict["Mask"] = ColorKeyMask(png, key);
            return new PdfStream(dict, png.Idat);
        }

        var (color, alpha) = DecodeWithAlpha(png);
        int maskNumber = table.Allocate();
        table.Set(maskNumber, new PdfStream(new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = new PdfInteger(Width),
            ["Height"] = new PdfInteger(Height),
            ["ColorSpace"] = new PdfName("DeviceGray"),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["Filter"] = new PdfName("FlateDecode")
        }, PdfFilters.Deflate(alpha)));

        dict["ColorSpace"] = png.ColorType == 3 ? ColorSpaceFor(png) : new PdfName(png.ColorType == 4 ? "DeviceGray" : "DeviceRGB");
        dict["BitsPerComponent"] = new PdfInteger(png.ColorType == 3 ? png.BitDepth : 8);
        dict["Filter"] = new PdfName("FlateDecode");
        dict["SMask"] = new PdfReference(maskNumber, 0);
        return new PdfStream(dict, PdfFilters.Deflate(color));
    }

    private PdfObject ColorSpaceFor(PngInfo png)
    {
        if (png.ColorType == 3)
        {
            int entries = png.Palette!.Length / 3;
            return new PdfArray(new PdfObject[]
            {
                new PdfName("Indexed"), new PdfName("DeviceRGB"), new PdfInteger(entries - 1),
                new PdfString(png.Palette.AsSpan(0, entries * 3).ToArray(), isHex: true)
            });
        }
        return new PdfName(png.ColorType == 2 ? "DeviceRGB" : "DeviceGray");
    }

    private static PdfArray ColorKeyMask(PngInfo png, byte[] key)
    {
        var values = new List<PdfObject>();
        int samples = png.ColorType == 2 ? 3 : 1;
        int max = (1 << png.BitDepth) - 1;
        for (int i = 0; i < samples && i * 2 + 1 < key.Length; i++)
        {
            int value = Math.Min(max, BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(i * 2)));
            values.Add(new PdfInteger(value));
            values.Add(new PdfInteger(value));
        }
        return new PdfArray(values);
    }

    private (byte[] Color, byte[] Alpha) DecodeWithAlpha(PngInfo png)
    {
        int channels = png.ColorType switch { 4 => 2, 6 => 4, _ => 1 };
        int bitsPerPixel = channels * png.BitDepth;
        int rowLength = (Width * bitsPerPixel + 7) / 8;
        long expected = (long)(rowLength + 1) * Height;

        byte[] raw;
        using (var z = new ZLibStream(new MemoryStream(png.Idat), CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[65536];
            int read;
            while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > expected + 65536)
                    throw new PdfFormatException("The PNG image is damaged.");
                output.Write(buffer, 0, read);
            }
            raw = output.ToArray();
        }
        if (raw.Length < expected) throw new PdfFormatException("The PNG image is damaged.");

        byte[] pixels = PdfFilters.PngUnfilter(raw, rowLength, Math.Max(1, bitsPerPixel / 8));
        var alpha = new byte[Width * Height];

        if (png.ColorType == 3)
        {
            // Indexed color stays packed; alpha comes from the palette transparency table.
            var trns = png.Transparency!;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int bitOffset = x * png.BitDepth;
                    int b = pixels[y * rowLength + bitOffset / 8];
                    int index = (b >> (8 - png.BitDepth - bitOffset % 8)) & ((1 << png.BitDepth) - 1);
                    alpha[y * Width + x] = index < trns.Length ? trns[index] : (byte)255;
                }
            }
            return (pixels, alpha);
        }

        int colorChannels = channels - 1;
        int bytesPerSample = png.BitDepth / 8;
        var color = new byte[Width * Height * colorChannels];
        for (int y = 0; y < Height; y++)
        {
            int rowStart = y * rowLength;
            for (int x = 0; x < Width; x++)
            {
                int pixel = rowStart + x * channels * bytesPerSample;
                for (int c = 0; c < colorChannels; c++)
                    color[(y * Width + x) * colorChannels + c] = pixels[pixel + c * bytesPerSample]; // high byte of 16-bit samples
                alpha[y * Width + x] = pixels[pixel + colorChannels * bytesPerSample];
            }
        }
        return (color, alpha);
    }
}

public enum PdfPageSize
{
    /// <summary>Page has the image's physical size (from its DPI, 96 DPI when unknown).</summary>
    FitImage,
    A4,
    Letter
}

/// <summary>Placement of an image on its own page.</summary>
public sealed record PdfImagePageLayout(PdfPageSize PageSize = PdfPageSize.A4, double MarginPoints = 28.35)
{
    public static PdfImagePageLayout Default { get; } = new();

    /// <summary>Returns page size and the image transformation matrix [a b c d e f].</summary>
    internal (double Width, double Height, double[] Matrix) Compute(PdfImage image)
    {
        var (displayWidth, displayHeight) = image.DisplaySize;
        double pageWidth, pageHeight, boxWidth, boxHeight, x, y;

        if (PageSize == PdfPageSize.FitImage)
        {
            double dpiX = image.DpiX >= 10 ? image.DpiX : 96;
            double dpiY = image.DpiY >= 10 ? image.DpiY : 96;
            if (image.Orientation >= 5) (dpiX, dpiY) = (dpiY, dpiX);
            pageWidth = Math.Clamp(displayWidth * 72.0 / dpiX, 3, 14400);
            pageHeight = Math.Clamp(displayHeight * 72.0 / dpiY, 3, 14400);
            boxWidth = pageWidth;
            boxHeight = pageHeight;
            x = 0;
            y = 0;
        }
        else
        {
            (pageWidth, pageHeight) = PageSize == PdfPageSize.Letter ? (612.0, 792.0) : (595.28, 841.89);
            if (displayWidth > displayHeight) (pageWidth, pageHeight) = (pageHeight, pageWidth);
            double availableWidth = pageWidth - 2 * MarginPoints;
            double availableHeight = pageHeight - 2 * MarginPoints;
            double scale = Math.Min(availableWidth / displayWidth, availableHeight / displayHeight);
            boxWidth = displayWidth * scale;
            boxHeight = displayHeight * scale;
            x = (pageWidth - boxWidth) / 2;
            y = (pageHeight - boxHeight) / 2;
        }

        double w = boxWidth, h = boxHeight;
        double[] matrix = image.Orientation switch
        {
            2 => new[] { -w, 0, 0, h, x + w, y },
            3 => new[] { -w, 0, 0, -h, x + w, y + h },
            4 => new[] { w, 0, 0, -h, x, y + h },
            5 => new[] { 0, -h, -w, 0, x + w, y + h },
            6 => new[] { 0, -h, w, 0, x, y + h },
            7 => new[] { 0, h, w, 0, x, y },
            8 => new[] { 0, h, -w, 0, x + w, y },
            _ => new[] { w, 0, 0, h, x, y }
        };
        return (pageWidth, pageHeight, matrix);
    }
}
