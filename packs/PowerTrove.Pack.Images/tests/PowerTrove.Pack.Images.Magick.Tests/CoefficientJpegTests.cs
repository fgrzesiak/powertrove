using BitMiracle.LibJpeg.Classic;
using PowerTrove.Pack.Images.Magick;
using PowerTrove.Pack.Images;
using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using ImageMagick;

namespace PowerTrove.Infrastructure.Tests;

public class CoefficientJpegTests
{
    private static byte[] Fixture(int width = 32, int height = 16, bool progressive = false, ushort orientation = 1)
    {
        var pixels = new byte[width * height * 4]; new Random(73).NextBytes(pixels); for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        using var png = new MemoryStream(); PngEncoder.Encode(new RasterImage(width, height, pixels), png);
        using var image = new MagickImage(png.ToArray()) { Format = MagickFormat.Jpeg, Quality = 88 };
        if (progressive) image.Settings.Interlace = Interlace.Plane;
        var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, orientation); exif.SetValue(ExifTag.Make, "SyntheticCamera"); image.SetProfile(exif); image.Orientation = (OrientationType)orientation;
        return image.ToByteArray();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourQuarterTurns_PreserveEveryQuantizedCoefficient(bool progressive)
    {
        byte[] original = Fixture(progressive: progressive), current = original;
        for (int i = 0; i < 4; i++) current = await Transform(current, OrthogonalTransform.Rotate90);
        var before = Read(original); var after = Read(current);
        Assert.Equal((before.Width, before.Height), (after.Width, after.Height)); Assert.Equal(before.Coefficients, after.Coefficients);
        using var source = new MagickImage(original); using var result = new MagickImage(current);
        Assert.Equal("SyntheticCamera", result.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
        using var sourcePixels = source.GetPixels(); using var resultPixels = result.GetPixels();
        Assert.Equal(sourcePixels.ToByteArray(PixelMapping.RGB), resultPixels.ToByteArray(PixelMapping.RGB));
    }
    [Fact]
    public async Task QuarterTurn_TransposesAndChangesSignsInTheCoefficientDomain()
    {
        byte[] input = Fixture(); var before = Read(input); byte[] result = await Transform(input, OrthogonalTransform.Rotate90); var after = Read(result);
        Assert.Equal((before.Height, before.Width), (after.Width, after.Height));
        var source = before.FirstComponent; var target = after.FirstComponent;
        int sourceBlockWidth = before.BlockWidth, sourceBlockHeight = before.BlockHeight;
        for (int y = 0; y < sourceBlockHeight; y++) for (int x = 0; x < sourceBlockWidth; x++)
        {
            var from = source[y * sourceBlockWidth + x]; var to = target[x * sourceBlockHeight + sourceBlockHeight - 1 - y];
            Assert.Equal(from[0], to[0]); Assert.Equal((short)-from[8], to[1]); Assert.Equal(from[1], to[8]); Assert.Equal((short)-from[9], to[9]);
        }
    }
    [Fact]
    public async Task ExifOrientation_IsResetWithoutLosingMake()
    {
        byte[] input = Fixture(orientation: 6); using var output = new MemoryStream();
        await new CoefficientJpegTransformer().TransformAsync(new MemoryStream(input), output, new LosslessImageRequest(OrthogonalTransform.Rotate90, 6), CancellationToken.None);
        var info = ImageProbe.Probe(output.ToArray()); Assert.Equal((16, 32, 1), (info.Width, info.Height, info.Orientation));
        using var image = new MagickImage(output.ToArray()); Assert.Equal("SyntheticCamera", image.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
    }
    [Fact]
    public async Task PartialEdgeBlocks_AreRefusedWithoutCroppingOrOutput()
    {
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<FileProcessingException>(() => new CoefficientJpegTransformer().TransformAsync(new MemoryStream(Fixture(31, 17)), output,
            new LosslessImageRequest(OrthogonalTransform.Rotate90, 1), CancellationToken.None));
        Assert.Contains("without cropping", error.Message); Assert.Equal(0, output.Length);
    }
    [Fact]
    public async Task CorruptionAndCancellation_AreSafeAndDoNotPublishMetadataValues()
    {
        using var output = new MemoryStream(); var bytes = Fixture(); Array.Fill(bytes, (byte)0, bytes.Length - 12, 12);
        var error = await Assert.ThrowsAsync<FileProcessingException>(() => new CoefficientJpegTransformer().TransformAsync(new MemoryStream(bytes), output, new(OrthogonalTransform.Rotate90, 1), CancellationToken.None));
        Assert.DoesNotContain("SyntheticCamera", error.Message);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CoefficientJpegTransformer().TransformAsync(new MemoryStream(Fixture()), Stream.Null, new(OrthogonalTransform.Rotate90, 1), cts.Token));
    }
    private static async Task<byte[]> Transform(byte[] source, OrthogonalTransform transform)
    {
        using var output = new MemoryStream(); await new CoefficientJpegTransformer().TransformAsync(new MemoryStream(source), output, new(transform, 1), CancellationToken.None); return output.ToArray();
    }
    private sealed record State(int Width, int Height, int BlockWidth, int BlockHeight, short[][] FirstComponent, short[] Coefficients);
    private static State Read(byte[] bytes)
    {
        var decoder = new jpeg_decompress_struct();
        try
        {
            decoder.jpeg_stdio_src(new MemoryStream(bytes)); decoder.jpeg_read_header(true); var arrays = decoder.jpeg_read_coefficients();
            int maxH = decoder.Comp_info.Max(c => c.H_samp_factor), maxV = decoder.Comp_info.Max(c => c.V_samp_factor);
            var all = new List<short>(); var first = new List<short[]>(); int firstW = 0, firstH = 0;
            // jpeg_read_coefficients returns MAX_COMPONENTS slots; only Num_components are used.
            for (int c = 0; c < decoder.Num_components; c++)
            {
                int w = (decoder.Image_width * decoder.Comp_info[c].H_samp_factor + maxH * 8 - 1) / (maxH * 8);
                int h = (decoder.Image_height * decoder.Comp_info[c].V_samp_factor + maxV * 8 - 1) / (maxV * 8);
                if (c == 0) { firstW = w; firstH = h; }
                for (int y = 0; y < h; y++) foreach (var block in arrays[c].Access(y, 1)[0].Take(w))
                {
                    var values = Enumerable.Range(0, 64).Select(i => block[i]).ToArray(); all.AddRange(values); if (c == 0) first.Add(values);
                }
            }
            decoder.jpeg_finish_decompress(); return new(decoder.Image_width, decoder.Image_height, firstW, firstH, first.ToArray(), all.ToArray());
        }
        finally { decoder.jpeg_destroy(); }
    }
}
