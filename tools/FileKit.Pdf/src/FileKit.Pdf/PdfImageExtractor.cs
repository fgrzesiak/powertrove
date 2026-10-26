namespace FileKit.Pdf;

/// <summary>An embedded image prepared for saving, without page placement or rendering effects.</summary>
public sealed record PdfExtractedImage(string Extension, byte[] Data);

/// <summary>Extracts single-filter JPEG or 8-bit Gray/RGB Flate image XObjects.</summary>
public static class PdfImageExtractor
{
    public const int MaxImages = 10_000;

    /// <summary>Returns null for unsupported encodings, masks or custom sample decoding.</summary>
    public static PdfExtractedImage? TryExtract(PdfDocument document, PdfImageInfo image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        var stream = document.GetObject(image.Reference.ObjectNumber) as PdfStream
            ?? throw new PdfOperationException("Image stream not found.");
        var dictionary = stream.Dictionary;
        if (dictionary.GetName("Subtype") != "Image") throw new PdfOperationException("The object is not an image.");
        if (HasValue(document, dictionary["SMask"]) || HasValue(document, dictionary["Mask"]) ||
            HasValue(document, dictionary["Decode"]) || document.Resolve(dictionary["ImageMask"]) is PdfBoolean { Value: true } ||
            document.Resolve(dictionary["SMaskInData"]) is PdfInteger { Value: > 0 }) return null;

        var filter = document.Resolve(dictionary["Filter"]);
        if (filter is PdfArray array)
        {
            if (array.Count != 1) return null;
            filter = document.Resolve(array[0]);
        }
        string? name = (filter as PdfName)?.Value;
        if (name is not ("DCTDecode" or "DCT" or "FlateDecode" or "Fl")) return null;

        int width = Dimension(document, dictionary["Width"]);
        int height = Dimension(document, dictionary["Height"]);
        if (stream.Data.Length > PdfImageLimits.Default.MaxFileBytes)
            throw new PdfLimitExceededException("The encoded image exceeds the extraction limit.");

        if (name is "DCTDecode" or "DCT")
        {
            var jpeg = PdfImage.Load(stream.Data);
            if (jpeg.Kind != PdfImageKind.Jpeg || jpeg.Width != width || jpeg.Height != height)
                throw new PdfFormatException("JPEG dimensions do not match the PDF image.");
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfExtractedImage(".jpg", stream.Data);
        }

        if (document.Resolve(dictionary["BitsPerComponent"]) is not PdfInteger { Value: 8 }) return null;
        string? color = (document.Resolve(dictionary["ColorSpace"]) as PdfName)?.Value;
        int components = color switch { "DeviceGray" => 1, "DeviceRGB" => 3, _ => 0 };
        if (components == 0) return null;
        int pixelBytes = PdfPngWriter.ValidateDimensions(width, height, components);

        var parameters = document.Resolve(dictionary["DecodeParms"]);
        if (parameters is PdfArray parametersArray)
        {
            if (parametersArray.Count != 1) return null;
            parameters = document.Resolve(parametersArray[0]);
        }
        if (parameters is not null and not PdfDictionary) return null;
        var parms = parameters as PdfDictionary;
        long predictor = Integer(document, parms?["Predictor"], 1);
        if (predictor != 1 && predictor is not (>= 10 and <= 15)) return null;
        if (predictor != 1 &&
            (Integer(document, parms?["Colors"], 1) != components ||
             Integer(document, parms?["Columns"], 1) != width ||
             Integer(document, parms?["BitsPerComponent"], 8) != 8)) return null;

        // Limit inflation to the exact expected rows, including predictor bytes.
        long expected = pixelBytes + (predictor == 1 ? 0L : height);
        byte[] decoded = PdfFilters.Inflate(stream.Data, expected);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.LongLength != expected) throw new PdfFormatException("Image sample count does not match the dimensions.");
        byte[] pixels = predictor == 1 ? decoded : PdfFilters.PngUnfilter(decoded, width * components, components);
        using var output = new MemoryStream();
        PdfPngWriter.Write(output, width, height, components, pixels, cancellationToken);
        return new PdfExtractedImage(".png", output.ToArray());
    }

    private static bool HasValue(PdfDocument document, PdfObject? value) => document.Resolve(value) is PdfObject resolved &&
        resolved is not PdfName { Value: "None" };

    private static int Dimension(PdfDocument document, PdfObject? value)
    {
        long dimension = Integer(document, value, 0);
        if (dimension <= 0) throw new PdfFormatException("Invalid image dimensions.");
        if (dimension > PdfPngWriter.MaxDimension) throw new PdfLimitExceededException("Image dimensions exceed the extraction limits.");
        return (int)dimension;
    }

    private static long Integer(PdfDocument document, PdfObject? value, long fallback) =>
        (document.Resolve(value) as PdfInteger)?.Value ?? fallback;
}
