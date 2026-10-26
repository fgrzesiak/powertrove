using System.Text;
using PowerTrove.Pack.Images.Magick;
using PowerTrove.Pack.Images;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using ImageMagick;

namespace PowerTrove.Infrastructure.Tests;

public class ImagePrivacyAndWatermarkTests
{
    private static byte[] Jpeg(bool gps)
    {
        using var image = new MagickImage(MagickColors.Blue, 32, 16) { Format = MagickFormat.Jpeg, Quality = 88 };
        var exif = new ExifProfile(); exif.SetValue(ExifTag.Make, "SyntheticCamera");
        if (gps)
        {
            exif.SetValue(ExifTag.GPSLatitudeRef, "N"); exif.SetValue(ExifTag.GPSLongitudeRef, "E");
            exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(50, 1), new Rational(1, 1), new Rational(2, 1) });
            exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(6, 1), new Rational(2, 1), new Rational(3, 1) });
        }
        image.SetProfile(exif); return image.ToByteArray();
    }
    [Fact]
    public async Task GpsRemoval_KeepsMakeAndRemovesGpsExifAndXmp()
    {
        using var sourceImage = new MagickImage(Jpeg(true));
        sourceImage.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:exif='http://ns.adobe.com/exif/1.0/' xmlns:tiff='http://ns.adobe.com/tiff/1.0/' exif:GPSLatitude='50,1N' tiff:Make='SyntheticCamera'><exif:GPSLongitude>6,2E</exif:GPSLongitude></rdf:Description></rdf:RDF></x:xmpmeta>")));
        using var output = new MemoryStream();
        await new MagickImageEngine().TransformAsync(new MemoryStream(sourceImage.ToByteArray()), output, new ImageTransformRequest(FileFormats.Jpeg, FileFormats.Jpeg, 0, null, null, false, false, true), CancellationToken.None);
        using var result = new MagickImage(output.ToArray()); var exif = result.GetExifProfile(); Assert.NotNull(exif);
        Assert.Equal("SyntheticCamera", exif.GetValue(ExifTag.Make)!.Value);
        Assert.DoesNotContain(exif.Values, v => v.Tag.ToString().StartsWith("GPS", StringComparison.OrdinalIgnoreCase));
        string xmp = Encoding.UTF8.GetString(result.GetXmpProfile()!.ToByteArray()); Assert.DoesNotContain("GPSLatitude", xmp); Assert.DoesNotContain("GPSLongitude", xmp); Assert.Contains("SyntheticCamera", xmp);
    }
    [Fact]
    public async Task NoGps_IsSkippedWithoutWriting()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<FileSkippedException>(() => new MagickImageEngine().TransformAsync(new MemoryStream(Jpeg(false)), output,
            new ImageTransformRequest(FileFormats.Jpeg, FileFormats.Jpeg, 0, null, null, false, false, true), CancellationToken.None));
        Assert.Equal(0, output.Length);
    }
    [Fact]
    public void GpsRequest_SelectsAnExplicitGpsCapableEngine()
    {
        var unsupported = new MetadataOnlyEngine(); var magick = new MagickImageEngine();
        var composite = new CompositeImageEngine(new IImageEngine[] { unsupported, magick });
        Assert.Same(magick, composite.Select(new ImageTransformRequest(FileFormats.Jpeg, FileFormats.Jpeg, 0, null, null, false, false, true)));
        Assert.Null(composite.Select(new ImageTransformRequest(FileFormats.Jpeg, FileFormats.Jpeg, 0, null, null, false, true, true)));
    }
    [Theory]
    [InlineData("top-left")]
    [InlineData("top")]
    [InlineData("top-right")]
    [InlineData("left")]
    [InlineData("center")]
    [InlineData("right")]
    [InlineData("bottom-left")]
    [InlineData("bottom")]
    [InlineData("bottom-right")]
    public async Task ImageWatermark_ChangesTheExpectedRegionAndRespectsOpacity(string position)
    {
        using var source = new MemoryStream(); using var mark = new MemoryStream();
        PngEncoder.Encode(RasterOperations.Canvas(400, 300, new BgraColor(255, 0, 0)), source);
        PngEncoder.Encode(RasterOperations.Canvas(40, 20, new BgraColor(0, 0, 255)), mark);
        source.Position = 0; mark.Position = 0; using var output = new MemoryStream();
        await new MagickImageWatermarker().ApplyAsync(source, mark, output, new ImageWatermarkRequest(FileFormats.Png, null, position, 50, 5, "#FFFFFF", 25, FileFormats.Png), CancellationToken.None);
        using var result = new MagickImage(output.ToArray()); using var pixels = result.GetPixels();
        var (x, y) = MagickImageWatermarker.Placement(400, 300, 100, 50, 6, position);
        var center = pixels.GetPixel(x + 50, y + 25).ToArray(); Assert.InRange((int)center[0], 120, 140); Assert.InRange((int)center[2], 120, 140);
        var outside = pixels.GetPixel(0, 0).ToArray(); Assert.Equal((byte)0, outside[0]); Assert.Equal((byte)255, outside[2]);
    }
    [Fact]
    public async Task TextWatermark_FontFallbackAndTilingAreDeterministic()
    {
        if (!new[] { "Arial", "Liberation Sans", "DejaVu Sans" }.Any(f => MagickNET.FontFamilies.Contains(f, StringComparer.OrdinalIgnoreCase))) return;
        using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(500, 300, new BgraColor(0, 0, 0)), png);
        var marker = new MagickImageWatermarker("Missing-Synthetic-Font"); var request = new ImageWatermarkRequest(FileFormats.Png, "SAMPLE", "tiled", 70, 5, "#FFFFFF", 25);
        async Task<byte[]> Run()
        {
            using var input = new MemoryStream(png.ToArray()); using var output = new MemoryStream(); await marker.ApplyAsync(input, null, output, request, CancellationToken.None);
            using var result = new MagickImage(output.ToArray()); using var pixels = result.GetPixels(); return pixels.ToByteArray(PixelMapping.RGB)!;
        }
        var a = await Run(); var b = await Run(); Assert.Equal(a, b);
        Assert.Contains(a[..(a.Length / 2)], v => v > 0); Assert.Contains(a[(a.Length / 2)..], v => v > 0);
    }
    private sealed class MetadataOnlyEngine : IImageEngine
    {
        public string Name => "metadata-only"; public bool CanPreserveMetadata => true;
        public bool CanDecode(string f) => true; public bool CanEncode(string f) => true;
        public Task TransformAsync(Stream s, Stream d, ImageTransformRequest r, CancellationToken ct) => throw new InvalidOperationException();
    }
}
