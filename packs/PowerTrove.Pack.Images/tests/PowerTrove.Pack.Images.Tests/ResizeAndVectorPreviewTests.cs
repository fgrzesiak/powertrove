using FileKit.Imaging;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;
using ImageMagick;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images.Magick;

namespace PowerTrove.Pack.Images.Tests;

public sealed class ResizeAndVectorPreviewTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly ImageCanvasActionTests.RealPngCodec codec = new();
    public void Dispose() => directory.Dispose();
    private IAction Action(string id) => new ImagesPack(new CompositeImageEngine([new MagickImageEngine()]),
        FileActionServices.CreatePhysical(new FileSignatureVerifier()), rasterCodec: codec, svgRenderer: new MagickSvgRenderer())
        .Actions.Single(a => a.Descriptor.Id == "powertrove.image." + id);
    private string Source()
    {
        var raster = RasterOperations.Canvas(200, 100, new(0, 0, 0, 0));
        for (int y = 20; y < 80; y++) for (int x = 30; x < 170; x++)
        { int p = (y * 200 + x) * 4; byte c = (byte)(x < 100 ? 70 : 190); raster.Bgra[p] = raster.Bgra[p + 1] = raster.Bgra[p + 2] = c; raster.Bgra[p + 3] = 255; }
        using var png = new MemoryStream(); PngEncoder.Encode(raster, png); return directory.Write("generated.png", png.ToArray());
    }
    [Theory]
    [InlineData(false, "standard", 200, 100)]
    [InlineData(true, "standard", 400, 200)]
    [InlineData(true, "smart", 400, 200)]
    public async Task ResizePreviewAndExportRetainAspectFormatAndSource(bool enlarge, string method, int width, int height)
    {
        string source = Source(); byte[] original = File.ReadAllBytes(source); var snapshot = Snapshots.ForPaths(source); var action = Action("resize");
        var values = new Dictionary<string,string> { ["MaxWidth"] = "400", ["MaxHeight"] = "400", ["AllowUpscale"] = enlarge.ToString(), ["Resampling"] = method };
        Assert.Null((await action.PreviewAsync(snapshot, new DictionaryParameters(values), default)).ImagePreview);
        var preview = await action.PreviewAsync(snapshot, ImagePreviewRequest.Create(values), default);
        Assert.False(preview.IsBlocked, preview.PreviewText); var frame = Assert.IsType<ImagePreviewData>(preview.ImagePreview);
        Assert.Equal((width,height), (frame.SourceWidth,frame.SourceHeight)); Assert.Equal((200,100), (frame.OriginalWidth,frame.OriginalHeight));
        Assert.True(frame.TryDecodePng(out _)); Assert.Empty(directory.NewFiles(source));
        var receipt = await action.ExecuteAsync(snapshot, new DictionaryParameters(values), null, default); Assert.True(receipt.IsSuccess, receipt.Message);
        using var image = new MagickImage(Assert.Single(receipt.OutputPaths)); Assert.Equal(((uint)width,(uint)height), (image.Width,image.Height));
        Assert.Equal(MagickFormat.Png, image.Format); Assert.True(image.HasAlpha); Assert.Equal(original,File.ReadAllBytes(source));
    }
    [Fact]
    public async Task LargeResizeUsesABoundedPreviewWithoutPretendingTheThumbnailIsTheExportResolution()
    {
        string source = Source(); var preview = await Action("resize").PreviewAsync(Snapshots.ForPaths(source),
            ImagePreviewRequest.Create(new Dictionary<string,string>{{"MaxWidth","4000"},{"MaxHeight","2000"},{"AllowUpscale","true"},{"Resampling","smart"}}),default);
        Assert.False(preview.IsBlocked,preview.PreviewText); var frame=preview.ImagePreview!;
        Assert.Equal((4000,2000),(frame.SourceWidth,frame.SourceHeight));Assert.Equal((512,256),(frame.PixelWidth,frame.PixelHeight));
        Assert.Equal((200,100),(frame.OriginalWidth,frame.OriginalHeight)); Assert.Empty(directory.NewFiles(source));
    }
    [Fact]
    public async Task VectorPreviewRendersTheGeneratedPathsAndRespondsToThresholdChanges()
    {
        string source=Source();var snapshot=Snapshots.ForPaths(source);var action=Action("png-to-svg");
        var values=new Dictionary<string,string>{{"Mode","mono"},{"Detail","medium"},{"Threshold","100"}};
        var first=await action.PreviewAsync(snapshot,ImagePreviewRequest.Create(values),default);
        values["Threshold"]="220";var second=await action.PreviewAsync(snapshot,ImagePreviewRequest.Create(values),default);
        Assert.False(first.IsBlocked,first.PreviewText);Assert.False(second.IsBlocked,second.PreviewText);
        Assert.NotEqual(first.ImagePreview!.PngBase64,second.ImagePreview!.PngBase64);Assert.True(second.ImagePreview.TryDecodePng(out var png));
        Assert.Empty(directory.NewFiles(source));
        var receipt=await action.ExecuteAsync(snapshot,new DictionaryParameters(values),null,default);Assert.True(receipt.IsSuccess,receipt.Message);
        string svg=File.ReadAllText(Assert.Single(receipt.OutputPaths));Assert.Contains("<path",svg);Assert.DoesNotContain("<image",svg);
        using var rendered=new MemoryStream();await new MagickSvgRenderer().RenderAsync(SvgRasterSource.Prepare(System.Text.Encoding.UTF8.GetBytes(svg),512,512),rendered,default);
        using var previewImage=new MagickImage(png);using var outputImage=new MagickImage(rendered.ToArray());
        Assert.Equal(outputImage.ToByteArray(MagickFormat.Rgba),previewImage.ToByteArray(MagickFormat.Rgba));
    }
    [Fact]
    public async Task HighQualityUpscalingIsExplicitAndRetainsAlpha()
    {
        string source=Source();byte[] bytes=File.ReadAllBytes(source);var engine=new MagickImageEngine();
        var request=new ImageTransformRequest(FileFormats.Png,FileFormats.Png,0,800,400,true,true);
        using var standard=new MemoryStream();using var smart=new MemoryStream();
        await engine.TransformAsync(new MemoryStream(bytes),standard,request,default);
        await engine.TransformAsync(new MemoryStream(bytes),smart,request with{HighQualityResize=true},default);
        using var a=new MagickImage(standard.ToArray());using var b=new MagickImage(smart.ToArray());
        Assert.Equal((800u,400u),(b.Width,b.Height));Assert.True(b.HasAlpha);
        Assert.NotEqual(a.ToByteArray(MagickFormat.Rgba),b.ToByteArray(MagickFormat.Rgba));
        using var pixels=b.GetPixels();Assert.Equal(0,pixels.GetPixel(0,0).ToColor()!.A);
    }
}
