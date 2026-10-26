using System.Buffers.Binary;
using System.Text;
using ContextBar.Infrastructure.Files;
using ContextBar.Pack.Images.Magick;
using ContextBar.Pack.Images;
using ImageMagick;

namespace ContextBar.Pack.Images.Tests;

public sealed class ImageActionsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());

    private static ImageExportAction Action(string id, IImageEngine engine) =>
        (ImageExportAction)new ImagesPack(new CompositeImageEngine(new[] { engine }), Services()).Actions.Single(a => a.Descriptor.Id == id);

    private string WriteImage(string name, uint width, uint height, MagickFormat format, Action<MagickImage>? configure = null)
    {
        using var image = new MagickImage(new MagickColor("#3366cc"), width, height) { Format = format };
        configure?.Invoke(image);
        return _dir.Write(name, image.ToByteArray());
    }

    [Fact]
    public async Task ConvertToWebp_WithMagick_CreatesVerifiedWebp()
    {
        string png = WriteImage("photo.png", 64, 32, MagickFormat.Png);

        var receipt = await Action("contextbar.image.convert-webp", new MagickImageEngine())
            .ExecuteAsync(Snapshots.ForPaths(png), Snapshots.Parameters(("Quality", "70")), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        string output = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("photo.webp"), output);
        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.WebP, result.Format);
        Assert.Matches("Saved|larger", receipt.Message);
    }

    [Fact]
    public async Task ConvertToJpeg_AppliesExifOrientation()
    {
        string jpeg = WriteImage("rotated.jpg", 80, 40, MagickFormat.Jpeg, img =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6); // rotate 90° clockwise to display
            img.SetProfile(exif);
            img.Orientation = OrientationType.RightTop;
        });
        Assert.Equal(6, (await FileKit.Imaging.ImageProbe.ProbeFileAsync(jpeg)).Orientation);

        var receipt = await Action("contextbar.image.convert-png", new MagickImageEngine())
            .ExecuteAsync(Snapshots.ForPaths(jpeg), new EmptyParameters(), null, CancellationToken.None);

        using var result = new MagickImage(Assert.Single(receipt.OutputPaths));
        Assert.Equal((40u, 80u), (result.Width, result.Height));
    }

    [Fact]
    public async Task RemoveMetadata_StripsExif_AndKeepsFormat()
    {
        string jpeg = WriteImage("gps.jpg", 32, 32, MagickFormat.Jpeg, img =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "SecretCam");
            img.SetProfile(exif);
        });

        var receipt = await Action("contextbar.image.remove-metadata", new MagickImageEngine())
            .ExecuteAsync(Snapshots.ForPaths(jpeg), new EmptyParameters(), null, CancellationToken.None);

        string output = Assert.Single(receipt.OutputPaths);
        Assert.Equal(_dir.File("gps.stripped.jpg"), output);
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile());
        Assert.DoesNotContain("SecretCam", Encoding.Latin1.GetString(File.ReadAllBytes(output)));
    }

    [Fact]
    public async Task Resize_FitsBoundingBox_AndDoesNotUpscale()
    {
        string large = WriteImage("large.png", 400, 200, MagickFormat.Png);
        string small = WriteImage("small.png", 50, 50, MagickFormat.Png);

        var receipt = await Action("contextbar.image.resize", new MagickImageEngine())
            .ExecuteAsync(Snapshots.ForPaths(large, small), Snapshots.Parameters(("MaxWidth", "100"), ("MaxHeight", "100")), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Message);
        using var resizedLarge = new MagickImage(_dir.File("large.resized.png"));
        using var resizedSmall = new MagickImage(_dir.File("small.resized.png"));
        Assert.Equal((100u, 50u), (resizedLarge.Width, resizedLarge.Height));
        Assert.Equal((50u, 50u), (resizedSmall.Width, resizedSmall.Height));
    }

    [Fact]
    public async Task PixelFlood_IsRejectedBeforeTheEngineRuns()
    {
        // Valid PNG header that declares 100000 x 100000 pixels.
        var png = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(png, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 100_000);
        string path = _dir.Write("bomb.png", png);
        var engine = new RecordingEngine();

        var receipt = await Action("contextbar.image.convert-webp", engine)
            .ExecuteAsync(Snapshots.ForPaths(path), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("The image dimensions exceed the supported limit.", Assert.Single(receipt.Failures).ErrorMessage);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task DisguisedFile_IsRejectedByContent()
    {
        string fake = _dir.Write("not-an-image.jpg", "%PDF-1.7 this is a PDF");
        var engine = new RecordingEngine();

        var receipt = await Action("contextbar.image.convert-webp", engine)
            .ExecuteAsync(Snapshots.ForPaths(fake), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Contains("does not match", Assert.Single(receipt.Failures).ErrorMessage);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task MissingEncoder_MakesActionUnavailable()
    {
        string png = WriteImage("a.png", 8, 8, MagickFormat.Png);
        var engine = new RecordingEngine(); // cannot encode AVIF

        var availability = await Action("contextbar.image.convert-avif", engine).MatchAsync(Snapshots.ForPaths(png), CancellationToken.None);

        Assert.False(availability.IsAvailable);
        Assert.Contains("AVIF", availability.Reason);
    }

    [Fact]
    public async Task Engines_AreChosenByCapability()
    {
        string png = WriteImage("a.png", 8, 8, MagickFormat.Png);
        var windowsLike = new RecordingEngine { Preserves = false };
        var fallback = new RecordingEngine { Preserves = true };
        var pack = new ImagesPack(new CompositeImageEngine(new IImageEngine[] { windowsLike, fallback }), Services());
        var resize = pack.Actions.Single(a => a.Descriptor.Id == "contextbar.image.resize");

        await resize.ExecuteAsync(Snapshots.ForPaths(png), Snapshots.Parameters(("StripMetadata", "false")), null, CancellationToken.None);
        await resize.ExecuteAsync(Snapshots.ForPaths(png), Snapshots.Parameters(("StripMetadata", "true")), null, CancellationToken.None);

        Assert.Equal(1, windowsLike.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Preview_WarnsAboutAnimationAndTransparency()
    {
        string gif = WriteImage("anim.gif", 8, 8, MagickFormat.Gif);

        var preview = await Action("contextbar.image.convert-jpeg", new MagickImageEngine())
            .PreviewAsync(Snapshots.ForPaths(gif), new EmptyParameters(), CancellationToken.None);

        Assert.Contains(preview.Warnings, w => w.Contains("first frame"));
        Assert.Contains(preview.Warnings, w => w.Contains("transparency"));
        Assert.Equal(_dir.File("anim.jpg"), Assert.Single(preview.ExpectedOutputs));
    }

[Fact]
    public void ActionStrings_AreLocalized_UnderGermanCulture()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var pack = new ImagesPack(new CompositeImageEngine(Array.Empty<IImageEngine>()), Services());
            foreach (var action in pack.Actions)
            {
                var desc = action.Descriptor;
                Assert.DoesNotContain("Convert to", desc.Title);
                Assert.DoesNotContain("Resize Images", desc.Title);
                Assert.DoesNotContain("Compress Images", desc.Title);
                Assert.DoesNotContain("Remove Image Metadata", desc.Title);

                Assert.DoesNotContain("Converts the selected images", desc.Description);
                Assert.DoesNotContain("Creates resized copies", desc.Description);
                Assert.DoesNotContain("Creates smaller copies", desc.Description);
                Assert.DoesNotContain("Creates copies without", desc.Description);

                if (desc.Parameters != null)
                {
                    foreach (var p in desc.Parameters)
                    {
                        Assert.DoesNotContain("Quality", p.Title);
                        Assert.DoesNotContain("Maximum width", p.Title);
                        Assert.DoesNotContain("Maximum height", p.Title);
                        Assert.DoesNotContain("Enlarge smaller", p.Title);
                        Assert.DoesNotContain("Remove metadata", p.Title);
                    }
                }
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void Presets_MapToProfiles()
    {
        var profile = ImagesPack.FromPreset("user.preset.web", "Web", "For the web", "avif", 55, 1200, 800, stripMetadata: true);
        Assert.Equal(ImageTarget.Avif, profile.Target);
        Assert.Equal(55, profile.QualityDefault);
        Assert.Equal(1200, profile.MaxWidthDefault);
        Assert.Null(new ImagesPack(new CompositeImageEngine(Array.Empty<IImageEngine>()), Services(), new[] { profile })
            .Actions.Last().Descriptor.Parameters);
    }

    /// <summary>Engine double that records calls and writes a tiny PNG.</summary>
    private sealed class RecordingEngine : IImageEngine
    {
        public int Calls { get; private set; }
        public bool Preserves { get; init; } = true;
        public string Name => "recording";
        public bool CanPreserveMetadata => Preserves;
        public bool CanDecode(string sourceFormat) => true;
        public bool CanEncode(string targetFormat) => targetFormat is FileFormats.Png or FileFormats.WebP or FileFormats.Jpeg;

        public async Task TransformAsync(Stream source, Stream destination, ImageTransformRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            using var image = new MagickImage(MagickColors.Red, 2, 2) { Format = MagickFormat.Png };
            await destination.WriteAsync(image.ToByteArray(), cancellationToken);
        }
    }
}

public sealed class ConvertWebpContractTests : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();

    protected override IAction CreateAction() =>
        new ImagesPack(new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() }), FileActionServices.CreatePhysical(new FileSignatureVerifier()))
            .Actions.Single(a => a.Descriptor.Id == "contextbar.image.convert-webp");

    protected override ContextSnapshot CreateValidSnapshot()
    {
        using var image = new MagickImage(MagickColors.Blue, 4, 4) { Format = MagickFormat.Png };
        return Snapshots.ForPaths(_dir.Write("a.png", image.ToByteArray()));
    }

    public void Dispose() => _dir.Dispose();
}
