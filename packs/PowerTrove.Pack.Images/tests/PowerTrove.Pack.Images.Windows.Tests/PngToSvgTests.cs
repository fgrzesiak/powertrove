using System.Xml.Linq;
using FileKit.Imaging.Raster;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Windows;

namespace PowerTrove.Pack.Images.Windows.Tests;

public sealed class PngToSvgTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    [Theory]
    [InlineData("color")]
    [InlineData("mono")]
    public async Task DefaultFactoryTracesGeneratedPngBatchAndRasterizesRealPathsWithHoles(string mode)
    {
        var pixels = RasterOperations.Canvas(32, 32, new(0, 0, 255));
        for (int y = 8; y < 24; y++) for (int x = 8; x < 24; x++) Array.Clear(pixels.Bgra, (y * 32 + x) * 4, 4);
        using var png = new MemoryStream(); var codec = new WindowsRasterCodec(); await codec.EncodeAsync(new(32, 32, pixels.Bgra), png, FileFormats.Png, 100, default);
        byte[] original = png.ToArray(); string first = _directory.Write("one.png", original), second = _directory.Write("two.png", original); _directory.Write("one.svg", "existing");
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier())); var pack = new ImagesPackFactory().Create(host);
        var action = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.image.png-to-svg"); var context = Snapshots.ForPaths(first, second); var options = Snapshots.Parameters(("Mode", mode), ("Detail", "high"));
        Assert.True((await action.MatchAsync(context, default)).IsAvailable); Assert.False((await action.PreviewAsync(context, options, default)).IsBlocked);
        var result = await action.ExecuteAsync(context, options, null, default); Assert.True(result.IsSuccess, result.Message); Assert.Equal(2, result.OutputPaths.Count);
        foreach (string output in result.OutputPaths)
        {
            string text = File.ReadAllText(output); var svg = XDocument.Parse(text); XNamespace ns = "http://www.w3.org/2000/svg";
            Assert.NotEmpty(svg.Root!.Elements(ns + "path")); Assert.DoesNotContain("<image", text); Assert.DoesNotContain("base64", text);
        }
        var render = pack.Actions.Single(a => a.Descriptor.Id == "powertrove.image.svg-to-png");
        var rendered = await render.ExecuteAsync(Snapshots.ForPaths(result.OutputPaths[0]), Snapshots.Parameters(("Width", "32"), ("Height", "32")), null, default); Assert.True(rendered.IsSuccess, rendered.Message);
        await using var stream = File.OpenRead(Assert.Single(rendered.OutputPaths)); var decoded = await codec.DecodeAsync(stream, FileFormats.Png, 1024, default);
        Assert.Equal(0, decoded.Bgra32[(16 * 32 + 16) * 4 + 3]); Assert.Equal(255, decoded.Bgra32[(4 * 32 + 4) * 4 + 3]);
        if (mode == "color") Assert.True(decoded.Bgra32[(4 * 32 + 4) * 4 + 2] > 240);
        else Assert.True(decoded.Bgra32[(4 * 32 + 4) * 4] < 10);
        Assert.Equal(original, File.ReadAllBytes(first)); Assert.Equal("existing", File.ReadAllText(_directory.File("one.svg")));
    }
    [Fact]
    public async Task WrongSignatureAndTransparentOnlyInputsCreateNoSvg()
    {
        var host = new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new FileSignatureVerifier())); var action = new ImagesPackFactory().Create(host).Actions.Single(a => a.Descriptor.Id == "powertrove.image.png-to-svg");
        string invalid = _directory.Write("invalid.png", "not a PNG"); Assert.True((await action.PreviewAsync(Snapshots.ForPaths(invalid), new EmptyParameters(), default)).IsBlocked);
        using var png = new MemoryStream(); await new WindowsRasterCodec().EncodeAsync(new(4, 4, new byte[64]), png, FileFormats.Png, 100, default);
        string blank = _directory.Write("blank.png", png.ToArray()); var result = await action.ExecuteAsync(Snapshots.ForPaths(blank), new EmptyParameters(), null, default);
        Assert.False(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.False(File.Exists(_directory.File("blank.svg")));
    }
}
